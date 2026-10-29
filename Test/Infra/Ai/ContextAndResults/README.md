# AI evidence and result tests

Bounded context/selection, whole-answer validation and safe persisted result lifecycles.

## Contents

| File                                                       | Test or fixture types                                                                                                                                                       | Responsibility                                                                                            |
|------------------------------------------------------------|-----------------------------------------------------------------------------------------------------------------------------------------------------------------------------|-----------------------------------------------------------------------------------------------------------|
| [AnalysisContextTests.cs](AnalysisContextTests.cs)         | `AnalysisContextTests`                                                                                                                                                      | Verifies selection, serialized budget accounting and exact evidence grounding with synthetic diagnostics. |
| [AnalysisPersistenceTests.cs](AnalysisPersistenceTests.cs) | `AnalysisPersistenceTests`, `RetirementFailure`, `RenewalProbe`, `AuditLoggerProvider`, `AuditLogger`, `Fixture`, `ContextProxy`, `ReadPort`, `Provider`, `AnalysisContext` | Verifies real metadata persistence and authorized safe readback without contacting a provider.            |
| [AnalysisResultTests.cs](AnalysisResultTests.cs)           | `AnalysisResultTests`                                                                                                                                                       | Checks the provider-neutral safe-result boundary independently of transport and persistence.              |
| [AssessmentSelectionTests.cs](AssessmentSelectionTests.cs) | `AssessmentSelectionTests`                                                                                                                                                  | Verifies status focus, fixed request bounds, typed source separation and the v2 privacy contract.         |

## Coverage and navigation

AnalysisContextTests checks priority/deduplication, Unicode serialized budgets, real evidence IDs and bounded
numeric/trace signals. AssessmentSelectionTests covers automatic focus/frozen windows/source classification/pre-limit
filtering/v2 sections. AnalysisResultTests checks safe independent redacted copies and malformed metadata.
AnalysisPersistenceTests runs real worker/service metadata flows and readback.

Lease-coordinator tests share a FakeTimeProvider across the worker and fixture services. They enter the provider,
commit owner cancellation or prepare renewal, then explicitly advance the heartbeat. This verifies renewal/cancellation
ordering without relying on a five-second CI scheduling window; owned processing is stopped before fixture disposal.
The cancellation barrier is signaled when the provider's awaited operation observes cancellation, avoiding a race
between cancellation callbacks and registration disposal.
Verification waits and database checks share a bounded test deadline independent of simulated worker shutdown, so
assertions still execute after host cancellation. Temporary context-building callbacks are detached before fixture
disposal and capture the specific service and scalar identities they need.

## Fixtures and boundaries

Use synthetic diagnostic/provider data and owned SQLite state, not live LLM calls. Persistence cases include no-provider
skips, consent revocation/reload, fabricated evidence, safe failure/usage telemetry, retention, owner
deletion/cancellation, late results, retry rebuilds and lease coordination. Structural/reference validity does not prove
recommendation quality. Keep context in memory and distinguish result/receipt/budget/queue lifetimes.

## Running

From the repository root:

```powershell
dotnet test Test/Test.csproj --filter FullyQualifiedName~Test.Infra.Ai.ContextAndResults
```

Explicit integration gates still apply; skipped cases do not verify the corresponding external service.

## Related documentation

- [Parent module](../README.md)
- [Selection policy](../../../../Application/Ai/Context/README.md)
- [Validation](../../../../Application/Ai/Results/README.md)
- [Collection](../../../../Infrastructure/Ai/Context/README.md)
- [Worker/publication](../../../../Infrastructure/Ai/Processing/README.md)
