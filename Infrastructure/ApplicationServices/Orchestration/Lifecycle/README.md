# Deployment lifecycle

Status persistence, non-secret history capture, GitHub progress and verified-local startup reconciliation.

## Contents

| File                                                                   | Types or asset                   | Responsibility                                                                                                                                                                                                                         |
|------------------------------------------------------------------------|----------------------------------|----------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------|
| [DeploymentCleanupHostedService.cs](DeploymentCleanupHostedService.cs) | `DeploymentCleanupHostedService` | A hosted service that runs at application startup to clean up any deployments that are stuck in "Building" or "Starting" status, and synchronize the status of "Running" or "Stopped" deployments with the actual Docker daemon state. |
| [DeploymentReconciliation.cs](DeploymentReconciliation.cs)             | `DeploymentReconciliation`       | Reconciles verified local deployments against Docker while leaving remote Azure history unchanged.                                                                                                                                     |
| [DeploymentSnapshotCapture.cs](DeploymentSnapshotCapture.cs)           | `DeploymentSnapshotCapture`      | Captures an explicit whitelist of non-secret launch settings once per deployment.                                                                                                                                                      |
| [DeploymentStatusUpdater.cs](DeploymentStatusUpdater.cs)               | `DeploymentStatusUpdater`        | Persists deployment status transitions and notifies interested UI subscribers.                                                                                                                                                         |
| [GitHubWorkflowMonitor.cs](GitHubWorkflowMonitor.cs)                   | `GitHubWorkflowMonitor`          | Polls GitHub Actions workflow, job, and step state and publishes safe incremental diagnostics.                                                                                                                                         |

## Usage and integration

DeploymentStatusUpdater persists/notifies transitions. DeploymentSnapshotCapture whitelists launch facts and resolves
artifacts. GitHubWorkflowMonitor polls changed progress then completed-run logs. DeploymentCleanupHostedService starts
DeploymentReconciliation for local recovery.

## Contracts and lifecycle

Outcome/FinishedAt survive later stops. Snapshots exclude secrets and do not rebuild history from current settings.
Persisted failures drive database AI wakeups independently of notifications.

Reconciliation applies only to Local sources lacking cloud workflow/resource identity: absent Docker projects must not
stop/fail Azure launches. Startup retains existing nonblocking/catch/token behavior, not a new shutdown-await guarantee.

GitHub diagnostics failure does not rewrite workflow conclusion; checkpoint confirmation controls replay suppression.
SafeUpdateAsync retains nonthrowing compatibility and safe failure-type logging.

## Related documentation

- [Parent module](../README.md)
- [History](../../../../Domain/Entities/Deployments/README.md)
- [Checkpoints](../../../GitHub/Workflows/README.md)
- [Notifications](../../../../Application/Orchestration/Status/README.md)
- [Recovery tests](../../../../Test/Infra/Orchestration/DeploymentCleanupHostedServiceTests.cs)
- [Workflow tests](../../../../Test/Infra/GitHub/GitHubWorkflowMonitorTests.cs)
