# Orchestration

Deployment orchestration contracts, job queue primitives, and status notification contracts.

## Source inventory

- `DeploymentJob.cs`
- `DeploymentJobQueue.cs`
- `DeploymentJobWorker.cs`
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
