# Verified workflow ingress

Signature-authenticated GitHub events reduced to deduplicated durable correlation receipts.

## Contents

| File                                                 | Types or asset          | Responsibility                                                      |
|------------------------------------------------------|-------------------------|---------------------------------------------------------------------|
| [GitHubWebhookReceiver.cs](GitHubWebhookReceiver.cs) | `GitHubWebhookReceiver` | Authenticates GitHub events and persists only correlation metadata. |

## Usage and integration

GitHubWebhookReceiver implements IGitHubWebhookReceiver. The Web webhook endpoint passes exact body bytes and
signature/delivery/event headers; the receiver verifies them with configured GitHub App secret, accepts relevant
workflow metadata and persists the receipt. Cloud run monitoring applies it asynchronously.

## Contracts and lifecycle

Verify before trusting payload identities or storing a receipt. Delivery ID protects replay; stored data is selected
installation/repository/run/attempt/commit/branch/workflow/status metadata, not raw HTTP body or logs. Receiving a
signed unrelated workflow does not authorize updating a deployment; the monitor also matches recorded run scope.
Preserve cancellation and duplicate-delivery behavior.

## Related documentation

- [Parent module](../README.md)
- [Ingress contract](../../../Application/Abstractions/GitHub/README.md)
- [HTTP endpoint](../../../Web/Routes/Endpoints/GitHubAppWebhookEndpoint.cs)
- [Receipt consumer](../../ApplicationServices/Orchestration/CloudRuns/README.md)
- [Receipt model](../../../Domain/Entities/Cloud/README.md)
- [Signature/replay tests](../../../Test/Infra/GitHub/GitHubWebhookReceiverTests.cs)
