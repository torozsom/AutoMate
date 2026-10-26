# AI execution policy

Feature options, fail-closed configuration checks, shared budget validation, bounded retries and authored skip guidance.

## Contents

| File                                               | Types                                                       | Responsibility                                                                                              |
|----------------------------------------------------|-------------------------------------------------------------|-------------------------------------------------------------------------------------------------------------|
| [AiAnalysisOptions.cs](AiAnalysisOptions.cs)       | `AiAnalysisOptions`                                         | Configures analysis admission, provider egress, bounded execution and result retention policies.            |
| [AnalysisBudgetPolicy.cs](AnalysisBudgetPolicy.cs) | `AnalysisBudgetPolicy`                                      | Validates shared limit snapshots and converts exact eight-decimal monetary allowances to integer units.     |
| [AnalysisEgressPolicy.cs](AnalysisEgressPolicy.cs) | `AnalysisEgressPolicy`                                      | Fail-closed policy for the initially supported provider, processing geographies and context categories.     |
| [AnalysisRetryPolicy.cs](AnalysisRetryPolicy.cs)   | `TransientAnalysisProviderException`, `AnalysisRetryPolicy` | Safe transient signal carrying only a bounded numeric server delay, never an error body or inner exception. |
| [AnalysisSkipPolicy.cs](AnalysisSkipPolicy.cs)     | `AnalysisSkipReason`, `AnalysisSkipPolicy`                  | Finite owner-visible skip reasons; provider errors and configuration text never become result guidance.     |

## Usage and integration

AiAnalysisOptions is bound/validated by Web and consumed by Infrastructure admission/workers/adapters.
AnalysisEgressPolicy supplies common approval requirements and the legacy OpenAI regional route check; provider-specific
routing remains in Infrastructure. AnalysisBudgetPolicy validates shared limits and converts exact decimal amounts into
integer units. AnalysisSkipPolicy maps finite reasons to codes and safe guidance; AnalysisRetryPolicy chooses retry
delay.

## Contracts and lifecycle

Enabled, AutomaticAnalysisEnabled and ProviderEgressEnabled default off independently. Current egress requires approved
region/account cohort/data categories and project consent through the authorizer. Common policy allows us/eu and exactly
logs, metrics and traceCorrelation categories; it also validates model/input/output/timeout/retention bounds.
Configuration policy alone does not authorize a deployment.

Context defaults are 24,000 characters/48,000 bytes/12,000 conservative token units; output defaults to 8,192 requested
tokens including reasoning, timeout to 60 seconds. MaximumConcurrency defaults to one per process (supported maximum
sixteen); shared provider limits are separate. Lease duration defaults to 120 seconds and recovery acquisitions to
three. ResultRetentionDays defaults to ninety but does not override saved-result RetainUntilDeleted semantics.

DailyProjectLimit defaults to five; tenant daily/minute limits to 100/10; tenant/global provider concurrency to 2/16.
Monetary settings default to zero and currency to USD: execution requires configured positive worst-case reservations.
Units use eight decimal places (100,000,000 units/currency); the unit conversion assumes already-valid settings.
Reservations are independent of actual provider charges.

TransientAnalysisProviderException carries only fixed text and bounded numeric Retry-After. Retry defaults are two
retries, ten-second base and 300-second maximum. Delay adds bounded jitter/server minimums and returns null when
exhausted or unsupported; the caller persists scheduling. Recovery acquisitions are separately bounded. Skip reasons
preserve stable codes for quota, cohort, capacity and spending/configuration failures; unknown codes use safe
unavailable guidance.

## Related documentation

- [Parent module](../README.md)
- [Runtime gates](../../Abstractions/Ai/Policy/README.md)
- [Startup validation and registration](../../../Web/Configs/Hosting/ServiceConfiguration.cs)
- [Atomic reservations](../../../Infrastructure/Ai/Admission/AnalysisBudgetGuard.cs)
- [Retry tests](../../../Test/App/Ai/Policy/AnalysisRetryPolicyTests.cs)
- [Egress tests](../../../Test/Infra/Ai/Admission/AnalysisEgressPolicyTests.cs)
- [Budget tests](../../../Test/Infra/Ai/Admission/AnalysisBudgetTests.cs)
- [Operator rollout and shutdown](../../../docs/ai-analysis-operations.md)
