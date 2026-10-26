# Local deployment and supervision

Docker preparation/stop and host-owned per-deployment daemon/log/metric lifetimes.

## Contents

| File                                                                     | Types or asset                                             | Responsibility                                                                                                                                                                             |
|--------------------------------------------------------------------------|------------------------------------------------------------|--------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------|
| [LocalDeploymentLogStreamManager.cs](LocalDeploymentLogStreamManager.cs) | `LocalDeploymentLogStreamManager`, `TargetState`           | Host-owned supervision; every subscription is tracked, canceled and awaited on replacement/shutdown.                                                                                       |
| [LocalDeploymentOrchestrator.cs](LocalDeploymentOrchestrator.cs)         | `LocalDeploymentOrchestrator`, `LocalHostPortAvailability` | Orchestrates the process of deploying .NET projects locally by managing interactions with various services, including database, system scanning, project scanning, templating, and Docker. |
| [LocalDockerTargets.cs](LocalDockerTargets.cs)                           | `LocalDockerTargets`                                       | Captures collector inventories using the existing generated container naming rules.                                                                                                        |
| [LocalRuntimeRecoveryService.cs](LocalRuntimeRecoveryService.cs)         | `LocalRuntimeRecoveryService`                              | Restores supervisors for current local deployments; supervisors enforce runtime collection interest.                                                                                       |

## Usage and integration

LocalDeploymentOrchestrator persists identity, prepares scanner/templates, checks name/port conflicts and registers
LocalDockerTargets before Compose. LocalDeploymentLogStreamManager is the singleton hosted ILocalDeploymentDiagnostics
supervisor. LocalRuntimeRecoveryService restores missing current targets.

## Contracts and lifecycle

Latest Starting/Running local deployments collect automatically without viewer/preference gates. Targets hold
names/channels/ownership, not credentials. Each owns a scoped Docker source; policy reads use separate scopes. The
five-second policy loop restarts independent sources; recovery runs every fifteen seconds.

Stop reuses healthy supervision and persists Stopped before cleanup; failed Compose down fails the job. Retirement is
shared/awaited outside the registry gate, so unrelated projects proceed and late stops cannot remove replacements.
Shutdown cancels/awaits sources.

Diagnostic startup failure does not fail a valid launch. After deployment creation, failure persistence uses independent
cancellation handling. Logs use GUIDs and fixed guidance, not paths/configuration/provider bodies.

## Related documentation

- [Parent module](../README.md)
- [Port](../../../../Application/Orchestration/Local/README.md)
- [Docker](../../../Docker/README.md)
- [Supervision tests](../../../../Test/Infra/Orchestration/LocalDiagnosticSupervisionTests.cs)
- [Conflict tests](../../../../Test/Infra/Orchestration/LocalHostPortAvailabilityTests.cs)
- [Queue tests](../../../../Test/Infra/Orchestration/DeploymentJobWorkerTests.cs)
