# Orchestration

Deployment orchestration contracts, job queue primitives, and status notification contracts.
The in-process scheduler admits a CPU-aware number of local Docker builds (at least two) and four cloud deployments
concurrently by default.
One stop operation may run beside builds. Jobs for the same project stay ordered without occupying a lane while waiting;
local jobs also wait for a conflicting Compose name or host port, and cloud jobs sharing a repository branch stay ordered.
The queue accepts at most 100 waiting jobs and rejects excess requests with a clear error. It remains in memory, so
queued jobs are lost if the AutoMate host exits.

Self-hosted operators can set `DeploymentConcurrency:MaxLocalBuilds` to `-1` for the CPU-aware default, `0` for no
AutoMate build cap, or an explicit limit from 1–1024. Docker still consumes host CPU, memory, disk, and AutoMate
log/diagnostic resources for every active build. `MaxCloudDeployments` accepts 1–16 and `MaxQueuedJobs` accepts
1–1000. Use environment variables with `DeploymentConcurrency__` prefixes when appropriate.
Only job type, project ID, lane, and timing are logged; queued credentials are never included in scheduler telemetry.

## Source inventory

- `DeploymentJob.cs`
- `DeploymentJobQueue.cs`
- `DeploymentJobWorker.cs`
- `DeploymentConcurrencyOptions.cs`
- `QueuedDeploymentJob.cs`
- `DeploymentStatusNotifier.cs`
- `ICloudDeploymentOrchestrator.cs`
- `IDeploymentJobQueue.cs`
- `IDeploymentStatusNotifier.cs`
- `ILocalDeploymentOrchestrator.cs`

## Boundary

Keep this module independent of Infrastructure and Web. Provider-facing work crosses an interface in
Application/Abstractions.

## Related documentation

- [Solution navigation map](../../.agents/navigation.md)
