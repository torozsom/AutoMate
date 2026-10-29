# Project presenter lifecycle tests

Owner/route/renderer fences, authorized actions, terminal replay and saved-history isolation.

## Contents

| File                                                                                   | Test or fixture types                                                                                                       | Responsibility                                                                                               |
|----------------------------------------------------------------------------------------|-----------------------------------------------------------------------------------------------------------------------------|--------------------------------------------------------------------------------------------------------------|
| [ConfigurationVariableLoadingTests.cs](ConfigurationVariableLoadingTests.cs)           | `ConfigurationVariableLoadingTests`, `Scanner`                                                                              | Exercises the actual deployment form's scan/merge behavior without external project or cloud access.         |
| [ConnectedCloudDeploymentAdmissionTests.cs](ConnectedCloudDeploymentAdmissionTests.cs) | `ConnectedCloudDeploymentAdmissionTests`, `Authentication`                                                                  | Characterizes the two deployment pages' connection ordering and owner-fence differences at the new boundary. |
| [DashboardStatusTests.cs](DashboardStatusTests.cs)                                     | `DashboardStatusTests`, `RecordingNavigation`, `FixtureDashboard`                                           | Exercises background deployment notifications against a real renderer dispatcher.                            |
| [DeploymentFailurePrivacyTests.cs](DeploymentFailurePrivacyTests.cs)                   | `DeploymentFailurePrivacyTests`                                                                                             | Checks deployment preparation failures at the real component boundary.                                       |
| [DeploymentHistoryTests.cs](DeploymentHistoryTests.cs)                                 | `DeploymentHistoryTests`, `RecordingJs`, `HistoryStub`                                                                      | Checks history loading independently of provider availability and browser layout.                            |
| [ProjectDetailsNotificationTests.cs](ProjectDetailsNotificationTests.cs)               | `ProjectDetailsNotificationTests`, `ControlledQuery`, `FixturePage`                                         | Exercises overlapping metadata reads and notification faults on a real Blazor dispatcher.                    |
| [ProjectDetailsStopTests.cs](ProjectDetailsStopTests.cs)                               | `ProjectDetailsStopTests`                                                                                                   | Cloud projects must never enter the local filesystem/Docker stop path.                                       |
| [ProjectTerminalReplayStateTests.cs](ProjectTerminalReplayStateTests.cs)               | `ProjectTerminalReplayStateTests`                                                                                           | Characterizes replay acceptance at the extracted state boundary without inspecting component fields.         |
| [ProjectTerminalReplayTests.cs](ProjectTerminalReplayTests.cs)                         | `ProjectTerminalReplayTests`                                                                                                | Checks latest-deployment replay and delayed-reply fences without a live SignalR connection.                  |
| [WorkspaceOverviewLifecycleTests.cs](WorkspaceOverviewLifecycleTests.cs)               | `WorkspaceOverviewLifecycleTests`, `FixturePanel`, `FixtureAuthentication`, `ControlledQuery`, `Request` | Exercises identity changes and disposal against actual asynchronous overview read lifetime.                  |

## Coverage and navigation

DashboardStatus/ProjectDetailsNotification tests drive real dispatcher notifications and coalesced reads through bUnit.
WorkspaceOverviewLifecycle uses bUnit component disposal to verify cancellation. Component factories retain seeded
fixture state, while normal component injection resolves explicitly registered test ports. Renderer failures remain
asserted, and DashboardStatus verifies that background notifications cause a further render.
Replay/ReplayState tests cover ordered identity/cursor rules, late replies and bounded live windows. History tests
isolate logs from metric failure, channel/range changes and denied access.
Stop/FailurePrivacy/CloudAdmission/VariableLoading/OverviewLifecycle tests cover local-only actions, connection
ordering, safe errors, form merging and owner rebinding.

## Fixtures and boundaries

Use controlled Application ports and renderer/scoped services, not real Docker/cloud/SignalR. Preserve current-project
versus historical deployment identity and reject stale/disposed responses. UI visibility is not enough: cases assert
handlers reject foreign/inactive/remote stop paths. A restored metric snapshot must not freeze newer typed live values;
metric changes must not reset log paging.

## Running

From the repository root:

```powershell
dotnet test Test/Test.csproj --filter FullyQualifiedName~Test.Web.Projects
```

Explicit integration gates still apply; skipped cases do not verify the corresponding external service.

## Related documentation

- [Parent module](../README.md)
- [Current/history presenters](../../../Web/Components/Pages/Projects/README.md)
- [Inventory actions](../../../Web/Components/Pages/Workspace/README.md)
- [Replay/scope/admission seams](../../../Web/Services/README.md)
- [Focused reads](../../../Application/Data/Apps/README.md)
