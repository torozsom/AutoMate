# Persistence and query tests

EF model consistency, bounded project reads and production PostgreSQL workspace SQL.

## Contents

| File                                                       | Test or fixture types                                       | Responsibility                                                                                             |
|------------------------------------------------------------|-------------------------------------------------------------|------------------------------------------------------------------------------------------------------------|
| [PersistenceModelTests.cs](PersistenceModelTests.cs)       | `PersistenceModelTests`                                     | Verifies the extracted mappings still match the checked-in PostgreSQL model without contacting a database. |
| [ProjectDetailsQueryTests.cs](ProjectDetailsQueryTests.cs) | `ProjectDetailsQueryTests`, `QueryDb`, `Clock`              | Exercises the real bounded project reads, complete-history aggregates and authorization in relational SQL. |
| [WorkspaceQueryTests.cs](WorkspaceQueryTests.cs)           | `WorkspaceQueryTests`, `BatchPort`, `FixedClock`, `Fixture` | Runs production SQL projections in owned disposable PostgreSQL schemas, without provider requests.         |

## Coverage and navigation

PersistenceModelTests compares the runtime PostgreSQL model to the checked-in snapshot without connecting.
ProjectDetailsQueryTests uses relational fixtures for current graph/history totals/paging/ownership. WorkspaceQueryTests
verifies real SQL projections, newest-timestamp ties, filters, outcome/observation meaning, private metric batches and
complete-day boundaries.

## Fixtures and boundaries

WorkspaceQueryTests uses AiPostgresFact and AUTOMATE_AI_TEST_DB, creates owned generated schemas and executes
migrations. Other files do not require that external database. Missing resource queries must not erase metadata totals;
owner filtering applies before provider work. SQLite query assertions and a model diff are not substitutes for
PostgreSQL SQL/locking execution.

## Running

From the repository root:

```powershell
dotnet test Test/Test.csproj --filter FullyQualifiedName~Test.Infra.Data
```

Explicit integration gates still apply; skipped cases do not verify the corresponding external service.

### Workspace SQL acceptance

With Docker and cached postgres:17-alpine, run from the repository root:

```powershell
./deploy/verification/Test-WorkspacePostgres.ps1
```

The runner supplies/restores AUTOMATE_AI_TEST_DB for its child process and removes only its owned disposable
container/schema resources.

## Related documentation

- [Parent module](../README.md)
- [Runtime model](../../../Infrastructure/Data/README.md)
- [Queries](../../../Infrastructure/ApplicationServices/Data/Apps/README.md)
- [Owned workspace runner](../../../deploy/verification/Test-WorkspacePostgres.ps1)
- [Shared PostgreSQL gate](../Ai/Processing/README.md)

MigrationDiscoveryTests protects all 24 historical migration IDs, concrete types and forward operation metadata
without applying migrations. PersistenceModelTests still verifies that runtime mappings match the existing snapshot.
