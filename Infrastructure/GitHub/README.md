# GitHub integration

Repository/workflow APIs, installation credentials, secret encryption, verified webhooks and incremental progress
checkpoints.

## Contents

| Module                                     | Responsibility                                                               |
|--------------------------------------------|------------------------------------------------------------------------------|
| [Api](Api/README.md)                       | Repository/workflow operations, authenticated requests and repository cache. |
| [Authentication](Authentication/README.md) | GitHub App configuration, permission checks and installation tokens.         |
| [Secrets](Secrets/README.md)               | Sealed-box repository secret encryption and request models.                  |
| [Webhooks](Webhooks/README.md)             | Signed ingress and metadata-only deduplicated receipts.                      |
| [Workflows](Workflows/README.md)           | Provider projections, bounded log parsing and progress checkpoints.          |

## Usage and integration

Api implements IGitHubService. Authentication validates initiating-user repository permission and issues GitHub App
tokens. Secrets builds encrypted repository-secret requests. Webhooks verifies signed ingress and stores minimal
receipts. Workflows maps provider responses and handles bounded completed-run logs/checkpoints; lifecycle monitoring
consumes them.

## Contracts and lifecycle

While a workflow runs, publish changed job/step status. Download job logs/final archive only after completion and
process bounded normalized/redacted chunks. Diagnostic delivery failures must not change workflow conclusion. Signed
download redirects must not receive the OAuth header.

Tokens, repository/branch names, signed URLs, secrets and output are excluded from custom telemetry. Repository cache
stores DTO metadata only, not workflow logs or AI context. SaaS launch throttles are shared per installation through
durable cooldown metadata.

## Related documentation

- [Parent module](../README.md)
- [GitHub ports](../../Application/Abstractions/GitHub/README.md)
- [Shared projections](../../Domain/DTO/GitHub/README.md)
- [Workflow consumer](../ApplicationServices/Orchestration/Lifecycle/README.md)
- [Monitor tests](../../Test/Infra/GitHub/GitHubWorkflowMonitorTests.cs)
