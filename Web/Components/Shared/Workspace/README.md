# Workspace presenters

Owner-scoped Overview loading and reusable KPI/inventory/aggregate views.

## Contents

| File                                                               | Types or asset                         | Responsibility                                                                                      |
|--------------------------------------------------------------------|----------------------------------------|-----------------------------------------------------------------------------------------------------|
| [KpiCard.razor](KpiCard.razor)                                     | `KpiCard`                              | Presents one workspace indicator with its label, value and optional qualification.                  |
| [ProjectInventoryTable.razor](ProjectInventoryTable.razor)         | `ProjectInventoryTable`                | Presents paginated project inventory and delegates authorized deployment, stop and removal actions. |
| [ProjectInventoryTable.razor.cs](ProjectInventoryTable.razor.cs)   | `ProjectInventoryTable` partial class  | Component state, callbacks and lifecycle behavior.                                                  |
| [WorkspaceOverviewPanel.razor](WorkspaceOverviewPanel.razor)       | `WorkspaceOverviewPanel`               | Loads owner-scoped overview data through independent scopes and visibility-aware polling.           |
| [WorkspaceOverviewPanel.razor.cs](WorkspaceOverviewPanel.razor.cs) | `WorkspaceOverviewPanel` partial class | Component state, callbacks and lifecycle behavior.                                                  |
| [WorkspaceOverviewView.razor](WorkspaceOverviewView.razor)         | `WorkspaceOverviewView`                | Presents recorded deployment activity and resource aggregates with numeric alternatives.            |
| [WorkspaceOverviewView.razor.cs](WorkspaceOverviewView.razor.cs)   | `WorkspaceOverviewView` partial class  | Component state, callbacks and lifecycle behavior.                                                  |

## Usage and integration

WorkspaceOverviewPanel resolves the owner, loads IWorkspaceQuery in fresh scopes, handles status/auth changes and polls
while visible. WorkspaceOverviewView renders supplied recorded activity/resources/recent/attention data. KpiCard renders
a label/value/qualification. ProjectInventoryTable delegates Deploy/Stop/Remove callbacks to its authorized page owner.

## Contracts and lifecycle

Owner/range changes fence in-flight work and clear inappropriate prior-account state. Dispose polling, auth/status
subscriptions and JS module resources. Resource-statistic failure must not erase deployment counts. Success ratios
exclude Unknown outcomes rather than count them as failures; observations remain incomplete when indicated.

Inventory uses bounded server paging and row submitting/stop state; local/active/capability flags control presentation
but do not authorize actions. Numeric outcome/metric pagination is independent of inventory. Keep
availability/qualification visible and render meaningful numeric alternatives to charts.

## Related documentation

- [Parent module](../README.md)
- [Workspace projections](../../../../Application/Data/Apps/README.md)
- [Action owner](../../Pages/Workspace/README.md)
- [Scoped calls](../../../Services/README.md)
- [Owner/polling tests](../../../../Test/Web/Projects/WorkspaceOverviewLifecycleTests.cs)
- [Overview fixtures](../../../../Test/Web/Metrics/TelemetryPageRenderingTests.cs)
- [Query semantics](../../../../Test/Infra/Data/WorkspaceQueryTests.cs)
