# Docker operation and diagnostic ports

Local Docker operations plus non-secret target inventories for independently supervised diagnostics.

## Contents

| File                                                   | Types                                                                                                       | Responsibility                                                                                                                                                                      |
|--------------------------------------------------------|-------------------------------------------------------------------------------------------------------------|-------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------|
| [IDockerService.cs](IDockerService.cs)                 | `IDockerService`                                                                                            | Service interface for managing Docker operations. Provides methods for checking the availability of the Docker daemon and for building and deploying projects as Docker containers. |
| [LocalDockerDiagnostics.cs](LocalDockerDiagnostics.cs) | `DockerContainerTarget`, `DockerDeploymentTarget`, `ILocalDeploymentDiagnostics`, `IDockerDiagnosticSource` | Immutable non-secret ownership targets and ports for local diagnostic supervision/source monitoring.                                                                                |

## Usage and integration

IDockerService exposes daemon checks, build/start, Compose up/down, discovery, log/metric streaming and port lookup.
ILocalDeploymentDiagnostics registers target inventories and tracks active deployment operations.
IDockerDiagnosticSource supplies daemon/container/metric streams to that supervisor. Infrastructure DockerService and
LocalDeploymentLogStreamManager implement these boundaries.

## Contracts and lifecycle

DockerContainerTarget and DockerDeploymentTarget identify owned resources without Docker SDK models or credentials.
Treat registered inventories as immutable, including their container lists. Compose stop accepts an optional deployment
identity for correlated output. A target registration is not user authorization.

Long-running source methods accept cancellation. Docker operations return success flags, nullable identities or port
values as documented in source; do not infer runtime health from a successful build. Environment JSON and queued
configuration can contain secrets. SaaS uses a disabled Docker implementation and capability gates.

## Related documentation

- [Parent module](../README.md)
- [Docker implementation](../../../Infrastructure/Docker/Runtime/DockerService.cs)
- [Disabled host implementation](../../../Infrastructure/Docker/Runtime/DisabledDockerService.cs)
- [Diagnostic supervision](../../../Infrastructure/ApplicationServices/Orchestration/Local/LocalDeploymentLogStreamManager.cs)
- [Local workflows](../../Orchestration/Local/README.md)
- [Supervision tests](../../../Test/Infra/Orchestration/LocalDiagnosticSupervisionTests.cs)
- [Collector tests](../../../Test/Infra/Diagnostics/Docker/DockerCollectorTests.cs)
