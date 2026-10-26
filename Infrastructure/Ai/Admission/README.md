# AI admission and readiness

Fresh egress authorization, owner-account shared capacity/spend reservations and bounded local readiness.

## Contents

| File                                                                           | Types or asset                       | Responsibility                                                                                                 |
|--------------------------------------------------------------------------------|--------------------------------------|----------------------------------------------------------------------------------------------------------------|
| [AnalysisBudgetGuard.cs](AnalysisBudgetGuard.cs)                               | `AnalysisBudgetGuard`                | Serializes short metadata reservations; no database lock is held during context or provider execution.         |
| [AnalysisEgressAuthorizer.cs](AnalysisEgressAuthorizer.cs)                     | `AnalysisEgressAuthorizer`           | Reads fresh metadata for each gate; never trusts cached consent or caller-supplied tenant identifiers.         |
| [DeploymentAnalysisReadinessService.cs](DeploymentAnalysisReadinessService.cs) | `DeploymentAnalysisReadinessService` | Read-only local readiness checks; never resolves an LLM adapter, reads diagnostics or changes queue ownership. |

## Usage and integration

AnalysisEgressAuthorizer resolves current deployment ownership, saved consent and provider policy at each gate.
AnalysisBudgetGuard serializes admission/attempt reservations. DeploymentAnalysisReadinessService reports finite
configuration and queue availability without resolving an LLM adapter or loading diagnostics.

## Contracts and lifecycle

Admission uses short owner-before-project locking; PostgreSQL attempt reservations also use a transaction advisory lock
across instances. Locks end before provider work. Quota-consuming admissions survive result/project deletion through
owner budget entries; replay/active aliases do not consume allowance again.

The attempt guard rechecks a live Running lease, tenant/global capacity, UTC-day currency and worst-case cost. Each
retry/recovery lease reserves separately; the same live lease reuses its entry. Non-refundable reservations use
1/100,000,000 currency units. Expiry/cancellation releases counted capacity, not money; remote work can outlive local
cancellation. This is conservative accounting, not exact billing.

Readiness performs bounded untracked metadata reads even with AI disabled because dispatch/retention remain hosted.
Credential presence does not verify provider access/billing. It neither claims work, grants consent nor verifies write
permissions/trigger installation. Web supplies the probe cancellation deadline.

## Related documentation

- [Parent module](../README.md)
- [Gate contracts](../../../Application/Abstractions/Ai/Policy/README.md)
- [Limits and skip guidance](../../../Application/Ai/Policy/README.md)
- [Mappings](../../Data/README.md)
- [Reservation tests](../../../Test/Infra/Ai/Admission/AnalysisBudgetTests.cs)
- [PostgreSQL tests](../../../Test/Infra/Ai/Admission/AnalysisBudgetPostgresTests.cs)
- [Readiness tests](../../../Test/Infra/Ai/Admission/AnalysisReadinessTests.cs)
