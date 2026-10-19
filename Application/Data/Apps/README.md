# Project and workspace contracts

Application lifecycle commands and owner-scoped reads for project details, deployment history and workspace analytics.

## Contents

| File                                                         | Types                                                                                                                                                                                            | Responsibility                                                                                                     |
|--------------------------------------------------------------|--------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------|--------------------------------------------------------------------------------------------------------------------|
| [IApplicationService.cs](IApplicationService.cs)             | `IApplicationService`                                                                                                                                                                            | Service interface for managing projects. Provides methods for adding, retrieving, updating, and deleting projects. |
| [IDeploymentDetailsService.cs](IDeploymentDetailsService.cs) | `IDeploymentDetailsService`                                                                                                                                                                      | Owner-scoped metadata read for one historical deployment.                                                          |
| [IProjectDetailsQuery.cs](IProjectDetailsQuery.cs)           | `ProjectDeploymentSummary`, `ProjectDetailsReadModel`, `IProjectDetailsQuery`, `IProjectActionQuery`                                                                                             | Current project/action reads, complete-history outcome summary and bounded history projections.                    |
| [IWorkspaceQuery.cs](IWorkspaceQuery.cs)                     | `IWorkspaceQuery`, `ProjectInventoryRequest`, `ProjectInventoryRow`, `ProjectInventoryPage`, `WorkspaceRun`, `WorkspaceActivity`, `WorkspaceResource`, `WorkspaceAttention`, `WorkspaceOverview` | Owner-scoped Overview and project inventory queries with detached activity/resource/run projections.               |

## Usage and integration

IApplicationService imports/deletes applications and retains legacy full-graph reads. Prefer IProjectDetailsQuery for
current project metadata, complete-history outcome totals and bounded history; IProjectActionQuery supplies current
metadata for deploy/stop. IDeploymentDetailsService scopes an exact deployment. IWorkspaceQuery provides Overview and
searchable/filterable inventory projections.

## Contracts and lifecycle

ProjectDetailsReadModel holds a detached Domain-shaped graph, summary and history. The Infrastructure implementation
uses zero-based six-row history pages and retains newest-timestamp ties for compatibility. Inventory is one-based and
uses twenty-row pages in WorkspaceQuery. Outcome totals describe recorded completion, independently of runtime status.

WorkspaceOverview separates run/activity/resource/attention projections and can include detailed
MetricExplorationResult. The default absolute-range Overview overload approximates a day count; the real implementation
honors frozen windows. Do not assume every adapter implements detailed windows.

Consent edits are owner-authorized and do not queue analysis. The exact overload identifies application, owner and C#
project; the legacy overload selects the first configured project. Missing remote configuration is created only during
explicit consent editing. Returned model types do not authorize arbitrary IDs themselves.

## Related documentation

- [Parent module](../README.md)
- [Lifecycle implementation](../../../Infrastructure/ApplicationServices/Data/Apps/ApplicationService.cs)
- [Project query implementation](../../../Infrastructure/ApplicationServices/Data/Apps/ProjectDetailsQuery.cs)
- [Deployment authorization](../../../Infrastructure/ApplicationServices/Data/Apps/DeploymentDetailsService.cs)
- [Workspace projections](../../../Infrastructure/ApplicationServices/Data/Apps/WorkspaceQuery.cs)
- [Project read tests](../../../Test/Infra/Data/ProjectDetailsQueryTests.cs)
- [Workspace tests](../../../Test/Infra/Data/WorkspaceQueryTests.cs)
- [Ownership hierarchy](../../../Domain/Entities/Projects/README.md)
