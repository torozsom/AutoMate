# Deployment and cloud-run states

Independent classifications for runtime state, completion evidence and durable SaaS workflow progress.

## Contents

| File                                         | Types and responsibility                                                                            |
|----------------------------------------------|-----------------------------------------------------------------------------------------------------|
| [DeploymentStatus.cs](DeploymentStatus.cs)   | DeploymentStatus: Starting, Running, Stopped or Failed recorded runtime state.                      |
| [DeploymentOutcome.cs](DeploymentOutcome.cs) | DeploymentOutcome: Unknown, Succeeded or Failed completion evidence.                                |
| [CloudRunPhase.cs](CloudRunPhase.cs)         | CloudRunPhase: Queued, Preparing, AwaitingWorkflow, WorkflowRunning, Succeeded, Failed or TimedOut. |

## Usage and integration

DeploymentStatus and DeploymentOutcome belong to Deployment; CloudRunPhase belongs to
CloudDeploymentRun. [DeploymentStatusUpdater](../../../Infrastructure/ApplicationServices/Orchestration/Lifecycle/DeploymentStatusUpdater.cs)
and [CloudRunMonitorService](../../../Infrastructure/ApplicationServices/Orchestration/CloudRuns/CloudRunMonitorService.cs)
show lifecycle consumers.

## Contracts and compatibility

Running is recorded state, not a health guarantee. DeploymentOutcome preserves completion evidence independently, so
stopping a successful deployment does not erase its success. A succeeded cloud workflow is distinct from later runtime
availability.

Preserve numeric ordering for deployment status/outcome. CloudRunPhase currently uses a string EF conversion, so
renaming members can change stored values. Transitions and consistency across these dimensions are implemented by
lifecycle services.

## Related documentation

- [Enums index](../README.md)
- [Deployment history](../../Entities/Deployments/README.md)
- [Cloud execution](../../Entities/Cloud/README.md)
- [Persistence representations](../../../Infrastructure/Data/AutoMateModelConfiguration.cs)
- [Cloud lifecycle tests](../../../Test/Infra/Orchestration/CloudDeploymentRunServiceTests.cs)
- [Status presentation tests](../../../Test/Web/Projects/DashboardStatusTests.cs)
