# Project and workspace persistence

Lifecycle commands, exact-project consent and bounded owner-authorized metadata/analytics.

## Contents

| File                                                       | Types or asset                                          | Responsibility                                                                                                                                                                                            |
|------------------------------------------------------------|---------------------------------------------------------|-----------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------|
| [ApplicationService.cs](ApplicationService.cs)             | `ApplicationService`, `LocalAppInput`, `RemoteAppInput` | Service class responsible for managing projects within the application. It handles operations such as adding local and GitHub projects, and interacting with the database to persist project information. |
| [DeploymentDetailsService.cs](DeploymentDetailsService.cs) | `DeploymentDetailsService`                              | Returns detached owner-authorized metadata without loading credentials or environment values.                                                                                                             |
| [ProjectDetailsQuery.cs](ProjectDetailsQuery.cs)           | `ProjectDetailsQuery`                                   | Separates current-state metadata, history aggregates and bounded history paging in owner-scoped SQL.                                                                                                      |
| [WorkspaceQuery.cs](WorkspaceQuery.cs)                     | `WorkspaceQuery`                                        | Projects bounded owner-scoped metadata and aggregate queries without provider calls.                                                                                                                      |

## Usage and integration

ApplicationService imports local/GitHub projects, retains legacy graph reads and performs scoped deletion/consent edits.
ProjectDetailsQuery implements current-action/detail reads; DeploymentDetailsService selects safe exact-deployment
metadata. WorkspaceQuery produces inventory/Overview with optional metric exploration.

## Contracts and lifecycle

Consent edits verify application ownership/C# project membership. Explicit remote edits can create missing
configuration; missing local configuration returns false. The legacy overload selects the first configured project;
consent does not queue analysis.

Project details aggregates all outcomes in SQL and uses zero-based six-row history pages. Current graphs retain
newest-timestamp ties. Split PostgreSQL reads share repeatable-read consistency (SQLite serializable) unless a caller
owns the transaction. Inventory is one-based/twenty rows with no full graph/provider call.

Overview separates completion outcomes from runtime state and uses count-weighted observations. Resource failures do not
erase metadata totals. Deletion queues archive cleanup transactionally. Never project tokens/environment secrets into
read models.

## Related documentation

- [Parent module](../README.md)
- [Read contracts](../../../../Application/Data/Apps/README.md)
- [Deletion](../../../Diagnostics/Archive/README.md)
- [Detail tests](../../../../Test/Infra/Data/ProjectDetailsQueryTests.cs)
- [Workspace tests](../../../../Test/Infra/Data/WorkspaceQueryTests.cs)
- [Configuration tests](../../../../Test/Web/Projects/ConfigurationVariableLoadingTests.cs)
