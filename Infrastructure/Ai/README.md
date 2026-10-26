# Deployment analysis infrastructure

Persistence-backed AI admission, current authorization, bounded evidence collection, durable execution and provider
adapters.

## Contents

| Module                             | Responsibility                                                     |
|------------------------------------|--------------------------------------------------------------------|
| [Admission](Admission/README.md)   | Current egress checks, shared reservations and local readiness.    |
| [Context](Context/README.md)       | Ephemeral bounded assessment evidence and provenance.              |
| [Processing](Processing/README.md) | Admission/readback, fenced leases, retries, workers and retention. |
| [Providers](Providers/README.md)   | Exact-route provider selection and bounded Responses protocol.     |

## Usage and integration

Admission supplies current ownership/consent/reservation/readiness checks. Context assembles ephemeral selected
evidence. Processing owns result management, fenced queue/workers/retries and cleanup. Providers selects explicitly
registered routes and validates bounded Responses transport. Application/Ai owns portable selection, budgets and result
policy.

## Contracts and lifecycle

Account identity is the tenant in both profiles. Work rows contain IDs/leases, never prompts or diagnostic context. Each
processing slot owns a fresh scope/DbContext; no database lock spans a provider call. Default-off
feature/automatic/egress flags remain independent from approved account cohort, spending configuration and current
project consent. Result management remains owner-authorized when execution is disabled.

## Shared tenant limits and reserved spend (M8)

Admission and provider attempts share owner-account limits across projects. Reservations use eight-decimal integer
currency units, remain charged after uncertain/failed/canceled attempts and are not invoice reconciliation.
See [Admission](Admission/README.md) for locking and retention semantics.

## Provider access denial

Provider denial uses fixed guidance and allowlisted metadata, never response prose. It is separate from transient retry.
See [Providers](Providers/README.md) and [Application result policy](../../Application/Ai/Results/README.md).

## Related documentation

- [Parent module](../README.md)
- [Ports](../../Application/Abstractions/Ai/README.md)
- [Shared policies](../../Application/Ai/README.md)
- [Metadata models](../../Domain/Entities/Ai/README.md)
- [Architecture and upgrades](../../docs/ai-analysis.md)
- [Rollout and monitoring](../../docs/ai-analysis-operations.md)
