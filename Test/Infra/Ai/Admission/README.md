# AI admission and budget tests

Owner/project consent, stable requests, shared quota/spend/capacity and read-only readiness.

## Contents

| File                                                             | Test or fixture types                                                                                                                   | Responsibility                                                                                               |
|------------------------------------------------------------------|-----------------------------------------------------------------------------------------------------------------------------------------|--------------------------------------------------------------------------------------------------------------|
| [AnalysisAdmissionTests.cs](AnalysisAdmissionTests.cs)           | `AnalysisAdmissionTests`, `CancelingDiagnostics`, `NoLocalDiagnostics`, `WorkInsertFailure`, `Fixture`, `TestClock`, `AdmissionContext` | Verifies admission/idempotency/quotas through the production service and independent relational connections. |
| [AnalysisBudgetPostgresTests.cs](AnalysisBudgetPostgresTests.cs) | `AnalysisBudgetPostgresTests`, `LegacyBudgetContext`, `Fixture`                                                                         | Executes real ordered migrations and shared admission/reservation locks in disposable PostgreSQL schemas.    |
| [AnalysisBudgetTests.cs](AnalysisBudgetTests.cs)                 | `AnalysisBudgetTests`, `ReloadDuringSum`, `Fixture`, `TestClock`, `ChargeFailure`, `BudgetContext`                                      | Independent relational connections exercise cross-project quotas, rolling rate and reserved spend/capacity.  |
| [AnalysisConsentTests.cs](AnalysisConsentTests.cs)               | `AnalysisConsentTests`                                                                                                                  | Verifies exact-project consent persistence and authorization without diagnostic payload storage.             |
| [AnalysisEgressPolicyTests.cs](AnalysisEgressPolicyTests.cs)     | `AnalysisEgressPolicyTests`, `Monitor`, `Authorizer`                                                                                    | Credential-free approval fixtures and fail-closed geography/category policy coverage.                        |
| [AnalysisReadinessTests.cs](AnalysisReadinessTests.cs)           | `AnalysisReadinessTests`, `UnreachableProvider`, `SettingsMonitor`, `ReadOnlyCommands`                                                  | Verifies local policy, credential reload and real read-only queue/schema access without provider resolution. |

## Coverage and navigation

AnalysisAdmissionTests exercises concurrent admission, receipts/replay/conflicts, automatic wakeups, deletion survival
and immutable selection/history. AnalysisBudgetTests uses independent SQLite connections for
account-day/rate/spend/capacity races and rollback/reload behavior. Consent/EgressPolicy/Readiness tests cover exact C#
project scope, fail-closed approvals and metadata-only probes. AnalysisBudgetPostgresTests executes real
migrations/owner/advisory locks and backfill.

## Fixtures and boundaries

Default relational cases use owned SQLite data and synthetic ports. Readiness fixtures include a provider whose
construction fails, proving the probe does not resolve it. Production PostgreSQL semantics require AUTOMATE_AI_TEST_DB;
generated schemas/migrations must target an isolated test database, never an application database. Same-lease
reuse/recovery charges, canceled lock waits and receipt backfill are separate from SQLite coverage.

Concurrent admission batches are fixture operations: each request creates and asynchronously disposes an independent
context, and the batch settles only after every started request and its cleanup complete. SQLite batches retain
thread-pool concurrency; PostgreSQL batches retain independent asynchronous connections. Await each batch before
disposing its fixture. Verification after simulated cancellation uses a separate deadline from the canceled operation.

## Running

From the repository root:

```powershell
dotnet test Test/Test.csproj --filter FullyQualifiedName~Test.Infra.Ai.Admission
```

Explicit integration gates still apply; skipped cases do not verify the corresponding external service.

### PostgreSQL acceptance

From the repository root, with Docker and an already cached postgres:17-alpine image:

```powershell
./deploy/verification/Test-AiPostgres.ps1
```

The runner supplies/restores its own environment and removes only its labeled disposable resources. It does not invoke a
provider or certify production HA/encryption/billing.

## Related documentation

- [Parent module](../README.md)
- [Gate implementations](../../../../Infrastructure/Ai/Admission/README.md)
- [Admission implementation](../../../../Infrastructure/Ai/Processing/DeploymentAnalysisService.cs)
- [Metadata lifetimes](../../../../Domain/Entities/Ai/README.md)
- [Owned acceptance runner](../../../../deploy/verification/Test-AiPostgres.ps1)
