# Deployment status notifications

In-process project/status events that let presentation consumers refresh after deployment changes.

## Contents

| File                                                         | Types                       | Responsibility                                                                       |
|--------------------------------------------------------------|-----------------------------|--------------------------------------------------------------------------------------|
| [DeploymentStatusNotifier.cs](DeploymentStatusNotifier.cs)   | `DeploymentStatusNotifier`  | In-process publisher for deployment status changes consumed by Blazor UI components. |
| [IDeploymentStatusNotifier.cs](IDeploymentStatusNotifier.cs) | `IDeploymentStatusNotifier` | Publishes deployment status transitions to in-process subscribers.                   |

## Usage and integration

Inject IDeploymentStatusNotifier; DeploymentStatusNotifier implements synchronous publication to OnStatusChanged
subscribers. Producers call NotifyStatusChanged with project ID and Domain DeploymentStatus. Web project components
subscribe and reload current authorized metadata; composition registers the notifier for shared notifications.

## Contracts and lifecycle

The notifier snapshots the invocation list and invokes each handler separately. A failing/disposed UI subscriber is
logged by finite failure type and cannot prevent other handlers or deployment processing. Subscribers must unsubscribe
when disposed and marshal UI work to their renderer as needed.

Events carry project/status only, not deployment identity, persisted outcome or full state. They are refresh hints, not
a durable queue, cross-process bus or replay mechanism. Use authorized read services to establish current state. Queue
StateChanged is a separate scheduling signal.

## Related documentation

- [Parent module](../README.md)
- [Persisted-status producer](../../../Infrastructure/ApplicationServices/Orchestration/Lifecycle/DeploymentStatusUpdater.cs)
- [Execution-failure producer](../Jobs/DeploymentJobWorker.cs)
- [UI subscriber](../../../Web/Components/Pages/Projects/ProjectDetails.razor.cs)
- [Notifier registration](../../../Web/Configs/Hosting/ServiceConfiguration.cs)
- [Refresh/coalescing tests](../../../Test/Web/Projects/ProjectDetailsNotificationTests.cs)
