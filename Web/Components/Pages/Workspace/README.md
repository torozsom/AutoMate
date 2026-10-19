# Workspace entry pages

Public introduction/signed-in Overview and owner-bound Projects inventory.

## Contents

| File                                       | Types or asset    | Responsibility                                                                                            |
|--------------------------------------------|-------------------|-----------------------------------------------------------------------------------------------------------|
| [Dashboard.razor](Dashboard.razor)         | `Dashboard`       | Presents owner-scoped project inventory, deployment controls and workspace telemetry.                     |
| [Dashboard.razor.cs](Dashboard.razor.cs)   | `Dashboard`       | Owner-bound inventory/filter state, deployment/stop/removal actions and coalesced renderer notifications. |
| [Dashboard.razor.css](Dashboard.razor.css) | Scoped stylesheet | Scoped styling for Dashboard.                                                                             |
| [Home.razor](Home.razor)                   | `Home`            | Introduces AutoMate and links to authenticated workspace and repository workflows.                        |
| [Home.razor.css](Home.razor.css)           | Scoped stylesheet | Scoped styling for Home.                                                                                  |

## Usage and integration

Home maps / and shows the public introduction or WorkspaceOverviewPanel for a signed-in user. Dashboard maps /dashboard
and owns query-string search/source/status/sort/page state, project actions and configuration/removal dialogs. Shared
Workspace components render views while Application query ports supply bounded metadata.

## Contracts and lifecycle

Deploy/Stop/Remove actions reload authorized current metadata in fresh scopes. Stop is local/active/capability-gated;
pending stops disable competing row actions. Cloud admission uses the credential-hidden preparation boundary and
navigates within the interactive app to Project Details.

Status/queue notifications are renderer-dispatched, coalesced and observed. Owner changes clear/rebind state and reject
late responses; disposal unsubscribes. Keep inventory paging distinct from metric/outcome-table paging. UI row state is
not persisted deployment outcome or authorization.

## Related documentation

- [Parent module](../README.md)
- [Overview/inventory presenters](../../Shared/Workspace/README.md)
- [Cloud preparation](../../../Services/ConnectedCloudDeploymentAdmission.cs)
- [Queries](../../../../Application/Data/Apps/README.md)
- [Status tests](../../../../Test/Web/Projects/DashboardStatusTests.cs)
- [Owner/lifetime tests](../../../../Test/Web/Projects/WorkspaceOverviewLifecycleTests.cs)
- [Stop semantics](../../../../Test/Web/Projects/ProjectDetailsStopTests.cs)
