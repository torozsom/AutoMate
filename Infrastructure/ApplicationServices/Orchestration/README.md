# Local deployment diagnostic supervision

`LocalDeploymentOrchestrator` persists the deployment before preparation and registers the labeled inventory before
Compose work. Safe phase, command and daemon observations use that deployment's Build channel. Stop operations register
lifecycle collection before Compose down, then cancel and await the project's sources. Diagnostic startup failure is
reported without failing an otherwise valid deployment.

Stop reuses an active deployment supervisor instead of rescanning dependencies and replacing healthy runtime sources.
Successful Compose down persists Stopped before diagnostic cleanup, preventing runtime recovery during slow cleanup.
Failed Compose down fails the queued stop job instead of recording a successful scheduler outcome.
Source retirement is shared per target and awaited outside the registry gate: other projects can register/stop while a
slow provider acknowledges cancellation. Replacements for the same project still await that retirement; a late stop
cannot remove a replacement. Command exit, finite outcome, source cleanup and job completion have safe platform logs.

`LocalDeploymentLogStreamManager` is a singleton hosted service implementing `ILocalDeploymentDiagnostics`. Its finite
registry tracks every daemon/log/metrics task and awaits cancellation on replacement, stop and host shutdown. Each
target
owns a scoped Docker source; policy reads use separate scopes. Daemon collection runs during deployment operations or
runtime interest. Runtime logs and metrics still require authorized viewers or explicit background consent. The
five-second
policy loop checks the latest deployment, restarts completed sources independently and cancels all runtime sources when
interest ends. No static state or detached `Task.Run` loops own subscription lifetimes.

`LocalRuntimeRecoveryService` registers missing supervisors for current running local deployments every 15 seconds.
Idle supervisors do not open runtime subscriptions; their policy loop responds to consent/viewing changes.
`LocalDockerTargets` stores names, service tabs and ownership IDs only, never credentials or environment payloads.

Docker details and bounded replay limitations are documented in [Docker](../../Docker/README.md).
Verification lives in `Infrastructure.Tests/Orchestration/LocalDiagnosticSupervisionTests.cs`.

Local deployment logging carries project/deployment GUID scopes and omits project names and filesystem paths from
start/solution lookup messages. The shared status updater reports persistence failure type without the exception body;
its non-throwing behavior and deployment notifications remain unchanged.

Local execution spans parent Compose/diagnostic children. GitHub spans correlate workflow/job/log phases. Deployment
admission/status, source supervision and checkpoints are unchanged; durable queue propagation remains M6 work.

Cloud preparation carries project/deployment GUID scopes. Scheduler, monitor, metrics, retention and cleanup failures
omit raw exceptions. Customer-visible preparation/run errors use fixed guidance instead of provider exception text;
status, retries and lease recovery are unchanged.

Automatic AI admission now comes from database failure capture using the persisted deployment ID, independently of
project-only UI events. It covers DeploymentStatusUpdater, SaaS cloud monitoring/processing and bulk startup cleanup.
Local failure outcome publication/persistence uses an independent token so caller cancellation after deployment creation
cannot suppress Failed persistence. Failures before any deployment is persisted do not invent an ID or fail an unrelated
latest deployment. The first cloud preparation diagnostic runs inside the existing failure handler.

Deployment failure handlers now pass exceptions to ILogger so the host can produce a bounded redacted console snapshot.
The shared ordinary-provider/export boundary remains metadata-only. Customer-visible cloud/local failure guidance,
statuses, cancellation and retry behavior are unchanged.

## Deployment history update (2026-10-08)

DeploymentSnapshotCapture whitelists non-secret launch settings before provider side effects, then records discovered
runtime/commit/image facts. Current configuration edits never rewrite previous snapshots. Preparation outcomes persist
independently of later stops.
