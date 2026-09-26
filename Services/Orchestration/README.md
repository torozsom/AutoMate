# Orchestration

`Services/Orchestration` coordinates AutoMate's deployment workflows. It
connects persisted Core entities and DTOs to scanners, templates, Docker,
GitHub, Azure, and log streaming without placing infrastructure mechanics or
UI state inside the workflow coordinator.

The module contains two deployment pipelines:

- local Docker Compose deployment for locally discovered projects;
- cloud deployment preparation and GitHub Actions monitoring for remote
  repositories targeting Azure Container Apps.

It also owns the bounded deployment queue, background job worker, deployment
status notifications, startup reconciliation, and shared resource-name
normalization.

## Responsibilities

- Queue local deploy, cloud deploy, and local stop requests.
- Process queued jobs outside the Blazor component lifecycle.
- Locate and analyze local source projects before deployment.
- Generate and persist local deployment artifacts.
- Start and stop Docker Compose deployments.
- Create deployment history records and persist status transitions.
- Prepare Azure/GitHub cloud deployment inputs and commit generated files.
- Poll GitHub Actions and start Azure runtime streaming after success.
- Publish deployment status changes to in-process UI subscribers.
- Reconcile persisted local deployment status with Docker state at startup.
- Normalize names used by Docker, image tags, and Azure resources.

The module does not own:

- scanner implementation;
- Docker daemon or CLI mechanics;
- GitHub REST/Octokit integration;
- Azure ARM provisioning;
- template rendering;
- SignalR transport;
- Blazor component state or navigation.

## Module Structure

```text
Services/Orchestration/
├── ILocalDeploymentOrchestrator.cs
├── LocalDeploymentOrchestrator.cs
├── LocalDeploymentLogStreamManager.cs
├── ICloudDeploymentOrchestrator.cs
├── CloudDeploymentOrchestrator.cs
├── CloudDeploymentDefaults.cs
├── CloudDeploymentRequestValidator.cs
├── CloudRepositorySecretBuilder.cs
├── CloudCsProjectResolver.cs
├── GitHubWorkflowMonitor.cs
├── IDeploymentJobQueue.cs
├── DeploymentJob.cs
├── DeploymentJobQueue.cs
├── DeploymentJobWorker.cs
├── IDeploymentStatusNotifier.cs
├── DeploymentStatusNotifier.cs
├── DeploymentStatusUpdater.cs
├── DeploymentCleanupHostedService.cs
└── OrchestrationNameNormalizer.cs
```

## Deployment Entry Point: The Queue

Web components do not invoke long-running deployment orchestration directly.
They enqueue immutable job records through `IDeploymentJobQueue`:

| Job | Payload | Handler |
|---|---|---|
| `LocalDeploymentJob` | `DeploymentConfigDto` | `ILocalDeploymentOrchestrator.DeployLocalProjectAsync` |
| `CloudDeploymentJob` | `CloudDeploymentRequestDto` | `ICloudDeploymentOrchestrator.DeployCloudProjectAsync` |
| `StopLocalDeploymentJob` | Project ID, project name, C# project path | `ILocalDeploymentOrchestrator.StopDeploymentAsync` |

`DeploymentJob.ProjectId` is the common correlation key for status updates,
logs, and UI state.

### Bounded queue behavior

`DeploymentJobQueue` uses a bounded `System.Threading.Channels.Channel`:

- capacity is 100 jobs;
- one reader and multiple writers are configured;
- `BoundedChannelFullMode.Wait` applies backpressure when full;
- enqueue observes the caller's cancellation token;
- dequeue ends when the worker's cancellation token is cancelled.

The queue is registered as a singleton, so all Web components and the hosted
worker share one in-process queue. It is not durable: queued jobs are lost
when the process stops or restarts.

### Worker behavior

`DeploymentJobWorker` is a hosted `BackgroundService` with a single consumer.
For each job it creates a fresh dependency-injection scope and resolves the
appropriate scoped orchestrator. A failure in one job is logged and does not
stop the worker from processing later jobs.

For failed local or cloud jobs, the worker publishes `DeploymentStatus.Failed`
when the orchestrator throws. Stop jobs do not receive this failure
notification because stopping is not represented as a new failed deployment.
Host shutdown cancellation is treated as normal worker shutdown.

If the worker itself exits unexpectedly, queued deployments resume only after
the host restarts; there is no persistent job store or retry scheduler in this
module.

## Local Deployment Pipeline

`LocalDeploymentOrchestrator` implements
`ILocalDeploymentOrchestrator.DeployLocalProjectAsync`.

```text
Web component
    -> LocalDeploymentJob
    -> DeploymentJobQueue
    -> DeploymentJobWorker
    -> LocalDeploymentOrchestrator
        -> load CsProject
        -> create Deployment
        -> locate solution root
        -> scan project metadata
        -> generate .automate artifacts
        -> Docker Compose up
        -> persist Running
        -> start log/metric streams
```

### Detailed sequence

1. Require a non-null `DeploymentConfigDto`.
2. Load the selected `CsProject` by `config.CsProjectId`.
3. Throw if the persisted project does not exist.
4. Create a `Deployment` with an image tag from the project name and ID.
5. Save the deployment and publish its initial status.
6. Resolve the solution root from the C# project path.
7. Create the solution's `.automate` directory when absent.
8. Scan project content and dependencies.
9. Render and save local deployment templates.
10. Persist `DeploymentStatus.Starting`.
11. Run Docker Compose with the configured project name.
12. Fail the workflow if Compose returns `false`.
13. Persist `DeploymentStatus.Running`.
14. Start background container log and metric streams.

The orchestrator owns the sequence and status transitions; `Services.Docker`
owns process execution, Docker output parsing, and daemon interactions.
`Services.Templating` owns generated file contents and
`Services.Scanner` owns source analysis.

### Local stop sequence

`StopDeploymentAsync`:

1. finds the solution root from the selected C# project path;
2. resolves the `.automate` directory;
3. returns without changing state when generated artifacts are absent;
4. runs Docker Compose down using the original project name;
5. stops active per-project log/metric workers when Docker succeeds;
6. loads the latest deployment for the application;
7. persists `DeploymentStatus.Stopped` when needed.

Stopping a deployment does not delete deployment history. The latest record
remains available for status and UI display.

### Local failure behavior

Unexpected failures after the deployment record is created are logged,
attempted to be persisted as `DeploymentStatus.Failed`, and rethrown. The
queue worker then logs the job failure and publishes a failure notification.

`DeploymentStatusUpdater.SafeUpdateAsync` protects the failure path from
`DbUpdateException`; the original workflow failure remains the important
failure signal. Persistence failures in normal status updates are allowed to
propagate through `UpdateAsync`.

## Local Stream Lifecycle

`LocalDeploymentLogStreamManager` owns per-project Docker stream workers:

- active cancellation sources are stored in a static concurrent dictionary;
- starting a new stream for the same project cancels and replaces the old one;
- each worker creates its own service scope for the scoped Docker service;
- web logs and metrics are started together;
- configured database logs and metrics are added separately;
- `StopAsync` cancels and removes the project's active worker;
- completed workers remove themselves only if they are still the active worker.

The manager publishes through `IDockerService`, which forwards output through
`ILogStreamer`. It does not know about SignalR groups or Blazor components.

Use the manager's replacement and cleanup rules when adding another local
runtime stream; do not create unmanaged `Task.Run` loops from UI components.

## Cloud Deployment Pipeline

`CloudDeploymentOrchestrator` implements
`ICloudDeploymentOrchestrator.DeployCloudProjectAsync`.

```text
Web component
    -> CloudDeploymentJob
    -> DeploymentJobQueue
    -> DeploymentJobWorker
    -> CloudDeploymentOrchestrator
        -> validate request
        -> apply cloud defaults
        -> resolve/create CsProject
        -> create Starting deployment
        -> prepare Azure OIDC and identity
        -> build and encrypt repository secret inputs
        -> generate cloud templates
        -> commit files to GitHub deployment branch
        -> poll GitHub Actions by commit SHA
        -> persist workflow run
        -> stream workflow logs
        -> start Azure runtime polling after success
```

### Detailed sequence

1. Validate repository root, owner, name, and GitHub access token.
2. Mark the configuration as a cloud deployment.
3. Apply default Azure region, resource group, Container App, and registry
   values.
4. Resolve the explicitly selected `CsProject`, or find/create a default web
   project for the remote application.
5. Create and save a `DeploymentStatus.Starting` record.
6. Configure Azure federated identity and required deployment resources.
7. Build the GitHub Actions secret dictionary.
8. Upsert repository secrets through `IGitHubService`.
9. Generate all cloud deployment files through `ITemplatingService`.
10. Reject an empty generated-file set.
11. Commit files to the requested deployment branch.
12. Store the commit SHA in `Deployment.ImageTag`.
13. Persist `DeploymentStatus.Running` and notify subscribers.
14. Poll GitHub Actions for a run matching the commit SHA.
15. Persist the workflow run ID when a run is found.
16. Mark the deployment failed and stream logs when a completed workflow
    conclusion is unsuccessful.
17. Stream successful workflow logs and start Azure runtime streaming after a
    successful completion.
18. Leave the deployment running with an informational message when polling
    ends before a completed run is observed.

The `Running` status currently represents that cloud artifacts were committed
and the workflow phase has started; the final workflow conclusion can still
move the deployment to `Failed`. Do not interpret `Running` as proof that the
Azure Container App is already serving traffic.

### Cloud project resolution

`CloudCsProjectResolver` uses the request's explicit `CsProjectId` when
provided. Otherwise it:

1. loads the remote application and its C# projects;
2. reuses the first existing web project;
3. creates a default web `CsProject` when none exists;
4. uses the repository root as the new project's path;
5. saves the new project before deployment creation.

The resolver owns persisted project association, not repository analysis or
template generation.

## Cloud Defaults and Secret Preparation

`CloudDeploymentDefaults` applies missing values without overwriting explicit
configuration:

| Setting | Default |
|---|---|
| Azure region | `Core.Defaults.DeploymentDefaults.AzureRegion` |
| Resource group | `<normalized-project>-<environment>-rg` |
| Container App | `<normalized-project>-<environment>-app` |
| Container registry | `ghcr.io` |

Environment suffixes map `production` to `prod`, `staging` to `stg`, and
`development` to `dev`. Other values are normalized as Azure resource-name
segments.

`CloudRepositorySecretBuilder` creates the secret values required by the
generated workflow:

- Azure client, tenant, and subscription IDs;
- a GHCR token, falling back to the GitHub access token;
- base64-encoded database credentials for supported login-based providers;
- base64-encoded custom environment variables in stable key order.

This builder decides secret names and value encoding for generated templates.
`Services.GitHub` performs repository public-key encryption and transport.
Never log the resulting dictionary or plaintext values.

## Workflow Monitoring

`GitHubWorkflowMonitor` owns cloud polling policy, not GitHub API mechanics:

- maximum of 60 polling attempts;
- ten-second delay between attempts;
- filters the relevant run by branch/workflow/commit through
  `IGitHubService`;
- streams only changed workflow status messages;
- returns a completed run immediately;
- returns the latest observed run when attempts are exhausted;
- downloads and streams workflow logs after completion.

GitHub-specific request construction, run mapping, and ZIP-log flattening are
documented in `Services/GitHub/README.md`. Orchestration decides when those
operations affect persisted deployment status and user-visible progress.

## Deployment Status

`DeploymentStatusUpdater` centralizes the common mutation sequence:

```text
deployment.Status = newStatus
    -> AutoMateDbContext.SaveChangesAsync
    -> IDeploymentStatusNotifier.NotifyStatusChanged
```

It exposes two paths:

- `UpdateAsync`: persistence failures propagate;
- `SafeUpdateAsync`: `DbUpdateException` is logged critically and does not
  escape the status-update call.

Use `UpdateAsync` when the active workflow must stop if state cannot be
persisted. Use `SafeUpdateAsync` only for best-effort cleanup or failure
reporting where the primary operation's exception should remain dominant.

`DeploymentStatusNotifier` is a singleton in-process publisher. It snapshots
the event delegate, invokes each subscriber independently, and logs failures
from disposed or faulty Blazor circuits without breaking other subscribers.
Web pages must unsubscribe from `OnStatusChanged` during disposal.

The notifier is not durable and does not replay the latest status to new
subscribers. Persisted deployment state remains the source of truth.

## Startup Reconciliation

`DeploymentCleanupHostedService` runs at host startup:

1. marks persisted `Starting` deployments as `Failed`;
2. loads persisted `Running` and `Stopped` deployments;
3. asks Docker for active Compose project names;
4. normalizes the expected project name from the application/project;
5. changes `Running` to `Stopped` when Compose is absent;
6. changes `Stopped` to `Running` when Compose is active;
7. saves changed records.

The cleanup uses a separate service scope because it is a hosted service and
must resolve scoped `AutoMateDbContext` and `IDockerService` safely.
Cancellation is logged normally; unexpected cleanup errors are logged
critically and do not crash the host.

This reconciliation covers local Docker state. Azure/GitHub workflow status
is handled during the cloud deployment workflow and runtime stream lifecycle.

## Name Normalization

`OrchestrationNameNormalizer` creates provider-compatible names:

| Method | Use |
|---|---|
| `NormalizeContainerName` | Docker container name segments; lowercases and replaces invalid runs with `-`. |
| `NormalizeComposeProjectName` | Docker Compose project names; allows letters, digits, `-`, and `_`. |
| `GenerateImageTag` | `automate-<safe-project>:<first-8-guid>` image tags. |
| `NormalizeResourceName` | Lowercase Azure-safe names capped at 23 characters. |

All normalizers use an `automate-project` or `automate-app` fallback when the
input has no usable characters. Keep the normalization deterministic because
the same project name must resolve to the same Docker and cloud resources
across deployment, stop, and cleanup operations.

## Dependency Injection

`Web.Configs.ServiceConfiguration.RegisterDomainServices` registers:

```csharp
services.AddScoped<ILocalDeploymentOrchestrator, LocalDeploymentOrchestrator>();
services.AddScoped<ICloudDeploymentOrchestrator, CloudDeploymentOrchestrator>();
services.AddSingleton<IDeploymentJobQueue, DeploymentJobQueue>();
services.AddHostedService<DeploymentJobWorker>();
services.AddSingleton<IDeploymentStatusNotifier, DeploymentStatusNotifier>();
services.AddHostedService<DeploymentCleanupHostedService>();
```

The orchestrators are scoped because they use scoped persistence and provider
services. The queue and status notifier are singletons because they must be
shared across Web circuits and the hosted worker. Hosted services create
scopes before resolving scoped dependencies.

## Error, Cancellation, and Logging Rules

- Validate deployment requests before external calls or database mutations
  whenever possible.
- Pass cancellation tokens through EF, scanner, templating, Docker, GitHub,
  Azure, and log-streaming operations.
- Preserve cancellation during active workflows; do not report a cancelled
  operation as successful.
- Treat queue capacity backpressure as expected behavior and let callers
  observe cancellation while waiting.
- Persist failure status when the deployment record exists, using
  `CancellationToken.None` only when failure reporting must survive the
  original cancellation.
- Keep workflow and provider failures explicit; do not convert failed Docker,
  GitHub, or Azure operations into successful deployment records.
- Never log access tokens, refresh tokens, repository secret values, database
  passwords, or custom environment values.
- Use project IDs, repository names, branch names, and safe resource names as
  diagnostic context.

The orchestration layer may publish user-facing progress through
`ILogStreamer`, but durable state must be saved through `AutoMateDbContext`.
Logs are not a substitute for deployment status or history.

## Consumers and Boundaries

| Consumer or dependency | Orchestration relationship |
|---|---|
| `Web.Components.Pages.Dashboard` | Validates prerequisites and enqueues local/cloud jobs; observes status events. |
| `Web.Components.Pages.ProjectDetails` | Enqueues local deploy/stop jobs and observes status/events. |
| `Services.Data` | Supplies `AutoMateDbContext` and persisted project/deployment state. |
| `Services.Scanner` | Resolves solution roots and analyzes project metadata. |
| `Services.Templating` | Generates local and cloud deployment files. |
| `Services.Docker` | Executes local Compose operations and runtime streams. |
| `Services.GitHub` | Commits cloud files, writes secrets, and reads workflow status/logs. |
| `Services.Azure` | Prepares OIDC/Azure resources and streams cloud runtime data. |
| `Services.LogStreaming` | Publishes build logs, container logs, and metrics. |
| `Core` | Defines DTOs, entities, enums, defaults, and persisted contracts. |

Keep orchestration policy here, but keep provider mechanics in their adapters.
Do not move Docker command construction, GitHub request headers, Azure ARM
payloads, or Scriban rendering into orchestrators.

## Testing Guidance

Test orchestration with provider interfaces replaced by fakes or mocks.
Important scenarios include:

- bounded queue backpressure and cancellation;
- worker isolation when one job fails;
- scoped dependency resolution per queued job;
- local deployment status sequence and failure persistence;
- missing local project or `.automate` directory behavior;
- replacement and cancellation of local stream workers;
- cloud default application without overwriting explicit values;
- cloud project reuse versus default web-project creation;
- secret dictionary ordering, database-provider recognition, and encoding;
- cloud status behavior for successful, failed, incomplete, and cancelled
  workflow runs;
- startup reconciliation of `Starting`, `Running`, and `Stopped` records;
- notifier isolation when one subscriber throws;
- deterministic name normalization and fallback values.

Do not require Docker, Azure, GitHub, or SignalR for unit tests of status,
queue, validation, defaulting, name normalization, or orchestration branching.
Use explicit integration tests for provider behavior.

## Extending the Module

When adding a deployment workflow or lifecycle operation:

1. Decide whether it is queue policy, workflow coordination, provider
   mechanics, persistence, or presentation transport.
2. Keep long-running operations behind a queued job when they should not run
   on the Blazor component lifecycle.
3. Add a focused interface only for a stable cross-module boundary.
4. Preserve project ID correlation across jobs, entities, logs, and status
   notifications.
5. Define status transitions and failure behavior before adding provider calls.
6. Use existing normalizers for resource names and existing status helpers for
   persistence/notifications.
7. Keep secrets in memory only as long as needed and pass them to the
   provider adapter without logging.
8. Update the relevant Core DTO/entity, provider README, Web consumer, tests,
   and this module documentation together.

Avoid adding provider SDK types or UI component references to this module.
Orchestration should coordinate abstractions, not become a second provider
implementation.

## File Map

| File | Purpose |
|---|---|
| `ILocalDeploymentOrchestrator.cs` | Local deploy and stop contract. |
| `LocalDeploymentOrchestrator.cs` | Local scan, template, Docker, persistence, and status workflow. |
| `LocalDeploymentLogStreamManager.cs` | Per-project local Docker log/metric worker lifecycle. |
| `ICloudDeploymentOrchestrator.cs` | Cloud deployment contract. |
| `CloudDeploymentOrchestrator.cs` | Azure preparation, GitHub artifacts, workflow monitoring, and cloud status workflow. |
| `CloudDeploymentDefaults.cs` | Default cloud region and resource names. |
| `CloudDeploymentRequestValidator.cs` | Required cloud request validation. |
| `CloudRepositorySecretBuilder.cs` | Builds encoded Azure, registry, database, and environment secrets. |
| `CloudCsProjectResolver.cs` | Reuses or creates the persisted cloud web project. |
| `GitHubWorkflowMonitor.cs` | Polls workflow runs and streams workflow status/logs. |
| `IDeploymentJobQueue.cs` | Queue abstraction. |
| `DeploymentJob.cs` | Local, cloud, and stop job records. |
| `DeploymentJobQueue.cs` | Bounded channel-backed queue. |
| `DeploymentJobWorker.cs` | Hosted single-reader job processor. |
| `IDeploymentStatusNotifier.cs` | In-process deployment status event contract. |
| `DeploymentStatusNotifier.cs` | Subscriber-isolated status publisher. |
| `DeploymentStatusUpdater.cs` | Status persistence and notification helper. |
| `DeploymentCleanupHostedService.cs` | Startup cleanup and Docker reconciliation. |
| `OrchestrationNameNormalizer.cs` | Deterministic Docker, image, and Azure name normalization. |

## Related Documentation

- [`Services`](../README.md)
- [`Services/Data`](../Data/README.md)
- [`Services/Docker`](../Docker/README.md)
- [`Services/GitHub`](../GitHub/README.md)
- [`Services/LogStreaming`](../LogStreaming/README.md)
- `Services/Azure` when its module README is added
- `Services/Scanner` when its module README is added
- `Services/Templating` when its module README is added
- [`Core/Entities`](../../Core/Entities/README.md)
- [`Core/DTO`](../../Core/DTO/README.md)
- [`Core/Enums`](../../Core/Enums/README.md)
- [`Web`](../../Web/README.md)
- [Solution navigation map](../../.agents/navigation.md)
