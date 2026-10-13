# Orchestration

Deployment orchestration contracts, job queue primitives, and status notification contracts.
In SaaS mode, `ICloudDeploymentRunService` admits authorized, idempotent cloud runs into PostgreSQL; its receipt
exposes queued age and cloud phase without credentials. `CloudSaasOptions` controls cluster admission. Cloud workflow
monitoring is separate from the launch lease. See [SaaS operations](../../docs/saas-cloud-deployments.md).
The in-process scheduler leaves independent local Docker builds and stops uncapped by default; Docker governs host
resource usage. Four cloud deployments may run concurrently by default.
Jobs for the same project stay ordered without occupying a lane while waiting;
local jobs also wait for a conflicting Compose name or host port, and cloud jobs sharing a repository branch stay
ordered.
The queue accepts at most 100 waiting jobs and rejects excess requests with a clear error. It remains in memory, so
queued jobs are lost if the AutoMate host exits.

Self-hosted operators can set `DeploymentConcurrency:MaxLocalBuilds` to `0` (the default) for no AutoMate build cap,
`-1` for the opt-in CPU-aware policy, or an explicit limit from 1–1024. `MaxLocalStops` defaults to `0` (uncapped)
and accepts an optional limit from 1–1024. Both settings require a host restart. Docker still consumes host CPU, memory,
disk, and AutoMate
log/diagnostic resources for every active build. `MaxCloudDeployments` accepts 1–16 and `MaxQueuedJobs` accepts
1–1000. Use environment variables with `DeploymentConcurrency__` prefixes when appropriate.
Only job type, project ID, lane, and timing are logged; queued credentials are never included in scheduler telemetry.
The queue records successful admission under its own source module before state notifications. Start/completion and
persisted deployment-status records distinguish accepted requests from running or completed work.

Self-hosted cloud jobs obtain fresh Azure ARM credentials when execution starts, rather than reusing the login token
captured when queued. Preparation failures are saved as redacted deployment output. Cloud application status remains
Starting until the workflow succeeds. Local runtime collectors resume automatically after host restart for active
deployments, independently of browser
viewing and legacy preference flags. AI diagnostic egress still requires separate current consent.

## Source inventory

- `Jobs/DeploymentJob.cs`
- `Jobs/DeploymentJobQueue.cs`
- `Jobs/DeploymentJobWorker.cs`
- `Jobs/DeploymentConcurrencyOptions.cs`
- `Jobs/QueuedDeploymentJob.cs`
- `Cloud/CloudDeploymentStart.cs`
- `Cloud/CloudSaasOptions.cs`
- `Cloud/CloudDeploymentAdmission.cs`
- `Cloud/CloudRuntimeConfigurationValidation.cs`
- `Status/DeploymentStatusNotifier.cs`
- `Cloud/ICloudDeploymentOrchestrator.cs`
- `Jobs/IDeploymentJobQueue.cs`
- `Status/IDeploymentStatusNotifier.cs`
- `Local/ILocalDeploymentOrchestrator.cs`

## Boundary

Keep this module independent of Infrastructure and Web. Provider-facing work crosses an interface in
Application/Abstractions.

## Related documentation

- [Solution navigation map](../../navigation.md)

Scheduler and status-subscriber failures export named failure types without exception objects. Subscriber isolation,
status
notifications, queue bounds and cancellation behavior remain unchanged.

Job failure exceptions now reach the host's separate redacted console diagnostics boundary. The ordinary provider/export
boundary still omits exception objects and prose; queue/status behavior and customer-facing guidance are unchanged.

Shutdown cancels queue consumption and observes any pending asynchronous read before disposing its iterator. Admitted
jobs finish cancellation and release their scopes before the worker exits. This prevents channel iterator disposal
faults during host shutdown without changing admission limits or per-project ordering.

## Folder organization

Folders group related files while retaining the module's existing .NET namespaces and public contracts.

- `Cloud/` — `Cloud/CloudDeploymentStart.cs`, `Cloud/CloudSaasOptions.cs`, `Cloud/GitHubWorkflowMonitoringOptions.cs`,
  `Cloud/ICloudDeploymentOrchestrator.cs`.
- `Jobs/` — `Jobs/DeploymentConcurrencyOptions.cs`, `Jobs/DeploymentJob.cs`, `Jobs/DeploymentJobQueue.cs`,
  `Jobs/DeploymentJobWorker.cs`, `Jobs/IDeploymentJobQueue.cs`, `Jobs/QueuedDeploymentJob.cs`,
  `Jobs/DeploymentResourceConflictException.cs`.
- `Local/` — `Local/ILocalDeploymentOrchestrator.cs`.
- `Status/` — `Status/DeploymentStatusNotifier.cs`, `Status/IDeploymentStatusNotifier.cs`.

## Deployment admission and validation

`Cloud/CloudDeploymentAdmission.cs` owns profile-specific request assembly behind existing queue/run ports.
Its request is memory-only and secret-bearing; it must never be logged. The confirmed mutable configuration is retained
for compatibility, so callers must not mutate it after admission. SaaS keys remain page-owned and are cleared only after
successful admission. Deployment metadata and template defaults are unchanged.

`Cloud/CloudRuntimeConfigurationValidation.cs` owns the existing form rules and first-error ordering. It is independent
of rendering and does not strengthen provider validation or add new server-side rejection rules.
