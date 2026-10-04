# Routes

Minimal API endpoint contracts and endpoint implementations.
The SaaS-only GitHub App webhook endpoint accepts bounded HTTPS POST bodies and delegates signature verification and
minimal metadata persistence to Infrastructure.

## Source inventory

- `IEndpoint.cs`

## Boundary

Keep presentation behavior here. Components, hubs, routes, and services use Application contracts; concrete
Infrastructure types are registered only in Web/Configs.

## Related documentation

- [Solution navigation map](../../.agents/navigation.md)

`Endpoints/DeploymentHistoryEndpoint.cs` provides owner-authorized cursor log pages and bounded metric-range APIs.

## Deployment telemetry update

Both SelfHosted and SaaS use the private Telemetry disk gateway for new deployment logs and metrics. PostgreSQL payload
writes are rejected at application startup; legacy reads and draining of existing outbox rows remain available. The
gateway confirms durable checksummed writes before cloud checkpoints advance. Tenant-scoped v2 history, deployment
revision recovery and weighted daily project analytics are documented in [the rollout guide](/docs/saas-telemetry.md).
Detailed data expires after 30 days; daily statistics after 365 days. See the root navigation.md for new module entry
points.
