# Deployment analysis ports

Provider-independent boundaries for ephemeral evidence, durable analysis operations, egress/budget gates and safe
results.

## Contents

| Module                           | Responsibility                                                          |
|----------------------------------|-------------------------------------------------------------------------|
| [Context](Context/README.md)     | Assessment preferences, ephemeral context and archive evidence queries. |
| [Execution](Execution/README.md) | Owner-facing analysis operations, fenced queue claims and readiness.    |
| [Policy](Policy/README.md)       | Current egress authorization and shared attempt-budget gates.           |
| [Results](Results/README.md)     | Provider invocation, untrusted responses and validation boundary.       |

## Usage and integration

Follow Execution for admission/readback/cancellation, Context for evidence and preferences, Policy for current
authorization/reservations, and Results for provider invocation and validation. Infrastructure coordinates these through
its worker and processor; Application/Ai supplies the policies.

## Contracts and lifecycle

Context and prompts are ephemeral; claims hold detached identities and lease metadata. Admission does not imply current
permission to send data: the worker and provider adapter independently recheck egress. Responses remain untrusted until
structural/redaction and selected-evidence validation succeed. Result management is separate from enabling provider
execution.

## Related documentation

- [Parent module](../README.md)
- [Policy implementations](../../Ai/README.md)
- [Workers and adapters](../../../Infrastructure/Ai/README.md)
- [Persisted metadata](../../../Domain/Entities/Ai/README.md)
- [Architecture and operations](../../../docs/ai-analysis.md)
