# AI integration-boundary tests

Admission/budget/consent, evidence/results, durable processing and provider protocol coverage.

## Contents

| Module                                           | Coverage                                                                 |
|--------------------------------------------------|--------------------------------------------------------------------------|
| [Admission](Admission/README.md)                 | Ownership/consent, idempotency, spend/capacity and readiness.            |
| [ContextAndResults](ContextAndResults/README.md) | Evidence/selection, validation and safe persisted result lifecycles.     |
| [Processing](Processing/README.md)               | Lease fencing, worker scopes/concurrency and PostgreSQL failure capture. |
| [Providers](Providers/README.md)                 | Exact provider routing, Responses protocol and policy reload.            |

## Coverage and navigation

Admission tests authorization/idempotency/account quotas and readiness. ContextAndResults tests bounded selected
evidence and safe persistence/readback. Processing tests leases, worker slots and automatic wakeups. Providers uses
synthetic HTTP to exercise exact routing, Responses parsing and reload cancellation. Pure retry math is under
App/Ai/Policy.

## Fixtures and boundaries

Default fixtures do not invoke an LLM. Relational SQLite tests use independent contexts where concurrency matters. Real
PostgreSQL lock/migration/trigger cases require AUTOMATE_AI_TEST_DB and owned schemas. Keep context/prompt payloads
ephemeral; result/receipt/budget lifetimes and provider-call counts are asserted separately.

## Running

From the repository root:

```powershell
dotnet test Test/Test.csproj --filter FullyQualifiedName~Test.Infra.Ai
```

Explicit integration gates still apply; skipped cases do not verify the corresponding external service.

## Related documentation

- [Parent module](../README.md)
- [Implementation map](../../../Infrastructure/Ai/README.md)
- [Contracts](../../../Application/Abstractions/Ai/README.md)
- [Retry policy tests](../../App/Ai/Policy/README.md)
- [Owned PostgreSQL runner](../../../deploy/verification/Test-AiPostgres.ps1)
