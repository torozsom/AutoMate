# GitHub integration ports

Repository/workflow operations, GitHub App installation credentials and signed webhook ingress.

## Contents

| File                                                   | Types                    | Responsibility                                                                         |
|--------------------------------------------------------|--------------------------|----------------------------------------------------------------------------------------|
| [IGitHubAppCredentials.cs](IGitHubAppCredentials.cs)   | `IGitHubAppCredentials`  | Issues installation-scoped GitHub App credentials and verifies user repository access. |
| [IGitHubService.cs](IGitHubService.cs)                 | `IGitHubService`         | Service interface for interacting with the GitHub API.                                 |
| [IGitHubWebhookReceiver.cs](IGitHubWebhookReceiver.cs) | `IGitHubWebhookReceiver` | Verifies and stores GitHub App webhooks without retaining raw payloads.                |

## Usage and integration

IGitHubService lists repositories, commits rendered files, finds marked commits, updates repository secrets, dispatches
workflows and reads workflow/job/archive output. IGitHubAppCredentials resolves repository installation identity and
creates installation tokens. IGitHubWebhookReceiver receives raw body bytes plus signature/delivery/event headers for
verification and durable processing.

## Contracts and lifecycle

User tokens, installation tokens, secrets and generated files can contain sensitive values. Do not log complete calls.
Preserve webhook body bytes for signature verification; receiving an event does not authorize it. Workflow status and
log availability remain separate projections.

Default branch/workflow values derive from Domain defaults. Cancellation must reach provider work.
Run/attempt/commit/branch correlation matters for matching the correct deployment rather than whichever workflow happens
to be latest. Downloads remain untrusted until normalization/redaction.

## Related documentation

- [Parent module](../README.md)
- [GitHub operations](../../../Infrastructure/GitHub/Api/GitHubService.cs)
- [Installation authentication](../../../Infrastructure/GitHub/Authentication/GitHubAppCredentials.cs)
- [Webhook verification](../../../Infrastructure/GitHub/Webhooks/GitHubWebhookReceiver.cs)
- [Provider projections](../../../Domain/DTO/GitHub/README.md)
- [Log retrieval tests](../../../Test/Infra/GitHub/GitHubServiceWorkflowLogTests.cs)
- [Cloud integration](../../../docs/saas-cloud-deployments.md)
