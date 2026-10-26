# Project and deployment consoles

Current project operations/live state and exact historical deployment output.

## Contents

| File                                                             | Types or asset                  | Responsibility                                                                                              |
|------------------------------------------------------------------|---------------------------------|-------------------------------------------------------------------------------------------------------------|
| [CloudDeploymentPageDefaults.cs](CloudDeploymentPageDefaults.cs) | `CloudDeploymentPageDefaults`   | Creates UI-layer default values used when deploying saved GitHub repositories without a local project scan. |
| [DeploymentHistory.razor](DeploymentHistory.razor)               | `DeploymentHistory`             | Presents owner-authorized saved deployment output, selected metrics and inline AI analysis.                 |
| [DeploymentHistory.razor.css](DeploymentHistory.razor.css)       | Scoped stylesheet               | Scoped styling for DeploymentHistory.                                                                       |
| [DeploymentMetricDisplay.cs](DeploymentMetricDisplay.cs)         | `DeploymentMetricDisplay`       | Formats the most recent typed observations for the project page's existing metric cards.                    |
| [ProjectDetails.razor](ProjectDetails.razor)                     | `ProjectDetails`                | Presents the owned current deployment, source-separated terminals, live metrics and analysis dialog.        |
| [ProjectDetails.razor.cs](ProjectDetails.razor.cs)               | `ProjectDetails`, `DatabaseTab` | Current-project metadata/queue/actions, fenced async refresh, live subscription/replay and disposal.        |
| [ProjectDetails.razor.css](ProjectDetails.razor.css)             | Scoped stylesheet               | Scoped styling for ProjectDetails.                                                                          |

## Usage and integration

ProjectDetails maps /project/{ProjectId}; code-behind owns current metadata, queue/status refresh, deploy/stop, SignalR
and bounded replay. DeploymentHistory maps /project/{ProjectId}/deployment/{DeploymentId} and independently loads
authorized saved logs/metrics/analysis. CloudDeploymentPageDefaults prepares remote-source form defaults;
DeploymentMetricDisplay formats latest typed observations.

## Contracts and lifecycle

Current metadata uses focused project query/action ports with existing compatibility fallbacks. History pages are six
rows; recorded outcome remains independent of runtime status. Next-launch configuration cannot reconstruct missing
historical snapshots. Stop is local-only and handlers reject remote sources even if UI controls are bypassed.

Join live groups only after project load and terminal render; buffer live arrivals during returned replay, deduplicate
by durable order/event identity and rejoin on reconnect. Page cancellation suppresses stale writes/cursor confirmation.
Queued notifications marshal/coalesce on the renderer and reject disposed generations; metadata failures preserve
terminals and offer retry.

History channel/page changes replace terminal content and restart filtered cursors. Metric reads have isolated
scopes/cancellation and cannot clear saved logs on failure. Azure cloud-web CPU/memory is Container App total, including
shared containers. Project section/dialog/terminal JS lifetimes are disposed with the presenter.

## Related documentation

- [Parent module](../README.md)
- [Replay bookkeeping](../../../Services/ProjectTerminalReplayState.cs)
- [Join authorization](../../../Hubs/README.md)
- [Read models](../../../../Application/Data/Apps/README.md)
- [Analysis presenter](../../Shared/Analysis/README.md)
- [Replay tests](../../../../Test/Web/Projects/ProjectTerminalReplayTests.cs)
- [Refresh tests](../../../../Test/Web/Projects/ProjectDetailsNotificationTests.cs)
- [History tests](../../../../Test/Web/Projects/DeploymentHistoryTests.cs)
- [Local stop tests](../../../../Test/Web/Projects/ProjectDetailsStopTests.cs)
