# Retry policy tests

Bounded retry timing independently of HTTP transport and durable queue scheduling.

## Contents

| File                                                       | Test or fixture types      | Responsibility                                                             |
|------------------------------------------------------------|----------------------------|----------------------------------------------------------------------------|
| [AnalysisRetryPolicyTests.cs](AnalysisRetryPolicyTests.cs) | `AnalysisRetryPolicyTests` | Verifies retry timing bounds independently of network/database scheduling. |

## Coverage and navigation

AnalysisRetryPolicyTests checks growing backoff, server minimums and refusal/exhaustion boundaries against Application
AnalysisRetryPolicy. Add cases here for changes to portable delay policy; test persisted future eligibility separately
under Infra/Ai/Processing.

## Fixtures and boundaries

No database/network/browser fixtures are used. Jitter means assertions should verify supported ranges/relationships
rather than an exact random value. A computed delay does not prove a provider attempt was persisted or will execute.

## Running

From the repository root:

```powershell
dotnet test Test/Test.csproj --filter FullyQualifiedName~Test.App.Ai.Policy
```

Explicit integration gates still apply; skipped cases do not verify the corresponding external service.

## Related documentation

- [Parent module](../README.md)
- [Policy](../../../../Application/Ai/Policy/AnalysisRetryPolicy.cs)
- [Queue/retry behavior](../../../Infra/Ai/Processing/README.md)
