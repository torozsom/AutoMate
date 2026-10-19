# Application AI policy tests

Focused tests for provider-independent execution policy.

## Contents

| Module                     | Coverage                                |
|----------------------------|-----------------------------------------|
| [Policy](Policy/README.md) | Bounded transient retry/backoff policy. |

## Coverage and navigation

Policy currently contains AnalysisRetryPolicyTests. Protocol, metadata queue and provider-adapter assertions live under
Infra/Ai, so a passing policy test does not establish durable retry scheduling.

## Fixtures and boundaries

Use deterministic bounds/relationships for jittered delays rather than require one random sample. No
HTTP/provider/database/browser infrastructure is needed. Add tests here when the policy belongs to Application rather
than an Infrastructure adapter.

## Running

From the repository root:

```powershell
dotnet test Test/Test.csproj --filter FullyQualifiedName~Test.App.Ai
```

Explicit integration gates still apply; skipped cases do not verify the corresponding external service.

## Related documentation

- [Parent module](../README.md)
- [Policy implementation](../../../Application/Ai/Policy/README.md)
- [Durable scheduling tests](../../Infra/Ai/Processing/README.md)
