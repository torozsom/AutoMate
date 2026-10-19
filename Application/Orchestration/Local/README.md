# Local deployment workflow

Application-facing local Docker deployment and stop operations.

## Contents

| File                                                               | Types                          | Responsibility                                                                                                                                      |
|--------------------------------------------------------------------|--------------------------------|-----------------------------------------------------------------------------------------------------------------------------------------------------|
| [ILocalDeploymentOrchestrator.cs](ILocalDeploymentOrchestrator.cs) | `ILocalDeploymentOrchestrator` | Service responsible for coordinating the end-to-end deployment process of local .NET projects, utilizing scanners, templating, and Docker services. |

## Usage and integration

ILocalDeploymentOrchestrator consumes prepared DeploymentConfigDto and returns a Deployment. StopDeploymentAsync takes
project identity, Compose project name and C# project path. Infrastructure LocalDeploymentOrchestrator coordinates
scanner/template/Docker operations and persistence; DeploymentJobWorker invokes it from capability-gated jobs.

## Contracts and lifecycle

These are execution operations, not queue admission. Forward cancellation through scanner, filesystem and Docker calls.
Configuration/environment/database settings can contain secrets. Project/path inputs must come from an authorized
workflow; the interface does not establish ownership itself.

Stop and deployment outcomes are distinct; stopping a successful deployment should retain its completion outcome.
Diagnostic supervision/recovery lives in Infrastructure and operates independently of browser viewing under current
runtime policy.

## Related documentation

- [Parent module](../README.md)
- [Implementation](../../../Infrastructure/ApplicationServices/Orchestration/Local/LocalDeploymentOrchestrator.cs)
- [Queue consumer](../Jobs/README.md)
- [Docker ports](../../Abstractions/Docker/README.md)
- [Launch settings](../../../Domain/DTO/Deployment/README.md)
- [History semantics](../../../Domain/Entities/Deployments/README.md)
- [Runtime supervision tests](../../../Test/Infra/Orchestration/LocalDiagnosticSupervisionTests.cs)
