# AI lease and worker tests

Atomic detached claims, bounded processing scopes, retry recovery and database failure capture.

## Contents

| File                                                                               | Test or fixture types                                                                       | Responsibility                                                                                                      |
|------------------------------------------------------------------------------------|---------------------------------------------------------------------------------------------|---------------------------------------------------------------------------------------------------------------------|
| [AnalysisQueueLeaseTests.cs](AnalysisQueueLeaseTests.cs)                           | `AnalysisQueueLeaseTests`, `Fixture`, `TestClock`, `QueueContext`                           | Uses independent relational connections to verify atomic acquisition, recovery and lease fencing.                   |
| [AnalysisWorkerConcurrencyTests.cs](AnalysisWorkerConcurrencyTests.cs)             | `AnalysisWorkerConcurrencyTests`, `Gate`, `Context`, `Provider`, `Fixture`, `WorkerContext` | Exercises hosted processing slots with production leases and independent relational scopes; no live provider calls. |
| [FailedDeploymentTriggerPostgresTests.cs](FailedDeploymentTriggerPostgresTests.cs) | `FailedDeploymentTriggerPostgresTests`, `AiPostgresFactAttribute`                           | Opt-in production failure-trigger execution/rollback plus shared AiPostgresFactAttribute gate.                      |

## Coverage and navigation

AnalysisQueueLeaseTests uses independent relational connections to test claim-once, token-fenced renewal/release, legacy
recovery, terminal exclusion, persisted retry deadlines and concurrent owner cancellation.
AnalysisWorkerConcurrencyTests checks slot limits/refill, independent scopes/instances and shutdown release.
FailedDeploymentTriggerPostgresTests executes the production trigger's commit/rollback behavior.

## Fixtures and boundaries

Default queue/worker cases use isolated relational fixtures and synthetic providers. Trigger execution is opt-in through
AiPostgresFactAttribute and AUTOMATE_AI_TEST_DB; it creates a disposable schema and executes the exact trigger without
applying migrations in that test. The separate budget acceptance tests cover ordered migrations. Do not infer PostgreSQL
trigger behavior from SQLite or skipped discovery.

## Running

From the repository root:

```powershell
dotnet test Test/Test.csproj --filter FullyQualifiedName~Test.Infra.Ai.Processing
```

Explicit integration gates still apply; skipped cases do not verify the corresponding external service.

## Related documentation

- [Parent module](../README.md)
- [Worker/queue](../../../../Infrastructure/Ai/Processing/README.md)
- [Production SQL](../../../../Infrastructure/Data/FailedDeploymentAnalysisTrigger.cs)
- [Migration acceptance](../Admission/README.md)
- [Owned runner](../../../../deploy/verification/Test-AiPostgres.ps1)
