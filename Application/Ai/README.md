# AI assessment policy

Provider-independent assessment selection, bounded evidence, execution policy and result validation.

## Contents

| Module                       | Responsibility                                                                 |
|------------------------------|--------------------------------------------------------------------------------|
| [Context](Context/README.md) | Bounded evidence selection, assessment ranges and reference membership.        |
| [Policy](Policy/README.md)   | Feature options, egress rules, retry delays, budgets and finite skip guidance. |
| [Results](Results/README.md) | Whole-response validation, redaction and safe provider failure categories.     |

## Usage and integration

Context canonicalizes assessment choices and selects redacted evidence. Policy defines options, egress configuration
checks, retry delays, exact monetary units and finite denial guidance. Results validates entire provider answers.
Infrastructure owns durable workers, storage and HTTP adapters; Web owns configuration binding and presentation.

## Contracts and lifecycle

AI enablement, automatic analysis and provider egress are independent default-off controls. Current operator policy,
approved account cohort and project consent are required for provider execution. Disabling AI does not disable
deployments or authorized result management.

Context selection is in-memory, not a new diagnostic snapshot store. Evidence membership establishes that a reference
was supplied, not that the narrative is correct. Saved-result lifetime and execution expiry are separate; consult
persisted metadata/retention consumers rather than inferring deletion from a context limit.

## Related documentation

- [Parent module](../README.md)
- [AI ports](../Abstractions/Ai/README.md)
- [Execution and provider implementations](../../Infrastructure/Ai/README.md)
- [Result and queue metadata](../../Domain/Entities/Ai/README.md)
- [Assessment contracts](../../docs/status-aware-assessments.md)
- [Operator controls](../../docs/ai-analysis-operations.md)
