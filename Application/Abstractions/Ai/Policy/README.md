# AI execution gates

Current egress authorization and durable shared attempt-budget reservations.

## Contents

| File                                                         | Types                       | Responsibility                                                                                         |
|--------------------------------------------------------------|-----------------------------|--------------------------------------------------------------------------------------------------------|
| [IAnalysisBudgetGuard.cs](IAnalysisBudgetGuard.cs)           | `IAnalysisBudgetGuard`      | Durably gates provider attempts by current ownership, shared concurrency and conservative daily spend. |
| [IAnalysisEgressAuthorizer.cs](IAnalysisEgressAuthorizer.cs) | `IAnalysisEgressAuthorizer` | Rechecks operator policy and current deployment-owner consent without retaining diagnostic payloads.   |

## Usage and integration

IAnalysisEgressAuthorizer rechecks deployment ownership, operator policy and saved project consent. Worker and provider
adapter invoke it independently. IAnalysisBudgetGuard reserves one claim's conservative maximum provider-attempt cost
after fresh context/egress checks and before provider resolution.

## Contracts and lifecycle

ReserveAttemptAsync returns null on successful reservation or a finite AnalysisSkipReason denial. It checks current
lease ownership, tenant/provider capacity and shared daily spend. Recovered attempts and retries use new ownership
tokens. These are non-refundable conservative reservations, not provider invoice reconciliation.

A valid evidence catalog, configured provider or successful readiness check does not grant egress. Do not cache an
earlier permission decision across attempts. Cancellation must reach persistence checks and provider gating; interface
implementations own atomicity and concurrency behavior.

## Related documentation

- [Parent module](../README.md)
- [Egress implementation](../../../../Infrastructure/Ai/Admission/AnalysisEgressAuthorizer.cs)
- [Budget implementation](../../../../Infrastructure/Ai/Admission/AnalysisBudgetGuard.cs)
- [Shared policy and options](../../../Ai/Policy/README.md)
- [Consent tests](../../../../Test/Infra/Ai/Admission/AnalysisConsentTests.cs)
- [Shared budget tests](../../../../Test/Infra/Ai/Admission/AnalysisBudgetTests.cs)
