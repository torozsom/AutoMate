# GitHub App credentials

Repository permission/installation verification and short-lived installation-token creation.

## Contents

| File                                               | Types or asset         | Responsibility                                                                    |
|----------------------------------------------------|------------------------|-----------------------------------------------------------------------------------|
| [GitHubAppCredentials.cs](GitHubAppCredentials.cs) | `GitHubAppCredentials` | Verifies repository access and issues short-lived GitHub App installation tokens. |
| [GitHubAppOptions.cs](GitHubAppOptions.cs)         | `GitHubAppOptions`     | GitHub App credentials supplied by the SaaS secret provider.                      |

## Usage and integration

GitHubAppCredentials implements IGitHubAppCredentials. It validates initiating-user repository access, resolves
installation/repository IDs and signs GitHub App authentication for installation tokens. GitHubAppOptions is supplied
through the SaaS host secret configuration.

## Contracts and lifecycle

AppId/AppSlug, PrivateKeyPem and WebhookSecret are operational credentials, not result/telemetry models. Never queue or
log installation tokens/private keys. Resolve repository write authorization with the initiating user before trusting
installation scope. Rate limiting becomes a RetryAt exception so durable workers can pause the installation rather than
blindly retry.

## Related documentation

- [Parent module](../README.md)
- [Credential port](../../../Application/Abstractions/GitHub/README.md)
- [Admission/cooldowns](../../ApplicationServices/Orchestration/CloudRuns/README.md)
- [Webhook secret consumer](../Webhooks/README.md)
- [SaaS admission tests](../../../Test/Infra/Orchestration/CloudDeploymentRunServiceTests.cs)
- [Configuration](../../../docs/saas-cloud-deployments.md)
