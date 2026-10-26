# In-memory deployment scheduling

Bounded deployment admission, resource-aware execution lanes and per-project queued/active state.

## Contents

| File                                                                             | Types                                                                                 | Responsibility                                                                                     |
|----------------------------------------------------------------------------------|---------------------------------------------------------------------------------------|----------------------------------------------------------------------------------------------------|
| [DeploymentConcurrencyOptions.cs](DeploymentConcurrencyOptions.cs)               | `DeploymentConcurrencyOptions`                                                        | Per-process limits for the in-memory deployment scheduler.                                         |
| [DeploymentJob.cs](DeploymentJob.cs)                                             | `DeploymentJob`, `LocalDeploymentJob`, `CloudDeploymentJob`, `StopLocalDeploymentJob` | Represents a queued deployment operation processed outside the Blazor component lifecycle.         |
| [DeploymentJobQueue.cs](DeploymentJobQueue.cs)                                   | `DeploymentJobQueue`                                                                  | Channel-backed deployment job queue with bounded capacity to avoid unbounded server memory growth. |
| [DeploymentJobWorker.cs](DeploymentJobWorker.cs)                                 | `DeploymentJobWorker`                                                                 | Admits independent deployment jobs with optional local caps and bounded cloud capacity.            |
| [DeploymentResourceConflictException.cs](DeploymentResourceConflictException.cs) | `DeploymentResourceConflictException`                                                 | A safe, actionable local resource conflict that can be shown in deployment output.                 |
| [IDeploymentJobQueue.cs](IDeploymentJobQueue.cs)                                 | `IDeploymentJobQueue`                                                                 | Queues deployment operations for background processing by the deployment worker.                   |
| [QueuedDeploymentJob.cs](QueuedDeploymentJob.cs)                                 | `QueuedDeploymentJob`, `DeploymentQueueState`                                         | A queued operation and its monotonic enqueue timestamp. Never log the job payload.                 |

## Usage and integration

Enqueue LocalDeploymentJob, CloudDeploymentJob or StopLocalDeploymentJob through IDeploymentJobQueue. DeploymentJobQueue
tracks queue state and StateChanged; DeploymentJobWorker consumes jobs and resolves scoped local/cloud orchestrators.
QueuedDeploymentJob carries the monotonic enqueue timestamp; DeploymentQueueState splits queued/active deployment and
stop counts. DeploymentResourceConflictException reports conflicting local resources from execution consumers.

## Contracts and lifecycle

Defaults are 100 queued jobs, four cloud executions, uncapped independent local builds and uncapped independent local
stops. MaxQueuedJobs clamps to 1–1000 and excess admission throws immediately. MaxCloudDeployments clamps to 1–16.
MaxLocalBuilds = 0 is uncapped, -1 selects max (2, CPU count/2), and positive values clamp to 1–1024. MaxLocalStops = 0
is uncapped; explicit limits clamp to 1–1024. Options are captured at construction; restart to change scheduling limits.

The worker preserves project order, avoids conflicting normalized Compose names/local host ports, and serializes cloud
jobs sharing repository/branch. Waiting jobs do not consume an execution lane. MarkStarted/MarkCompleted update
counters; queue state is not persisted deployment outcome.

Work is memory-only and lost on process exit. Request objects may contain credentials/mutable configuration: do not log
jobs or mutate them after admission. The worker gates host capabilities and creates/disposes one DI scope per admitted
execution. On shutdown it cancels pending reads, observes iterator work before disposal and waits for running scopes to
finish cancellation. Subscriber failures are isolated from admission.

This queue is distinct from durable SaaS cloud-run and AI queues; it provides neither cross-instance leases nor durable
retries.

## Related documentation

- [Parent module](../README.md)
- [Local execution port](../Local/README.md)
- [Cloud execution/admission](../Cloud/README.md)
- [Status notifications](../Status/README.md)
- [Hosted worker registration](../../../Web/Configs/Hosting/ServiceConfiguration.cs)
- [Scheduling/shutdown tests](../../../Test/Infra/Orchestration/DeploymentJobWorkerTests.cs)
- [Host-port conflict tests](../../../Test/Infra/Orchestration/LocalHostPortAvailabilityTests.cs)
