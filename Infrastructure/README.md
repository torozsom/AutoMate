# Infrastructure

EF Core persistence, external-system adapters, and Application-port implementations.

## Source inventory

- `Infrastructure.csproj`

## Boundary

Infrastructure may reference Application and Domain, but never Web. It implements ports and owns provider-specific
behavior.

## Related documentation

- [Domain](../Domain/README.md)`n- [Application](../Application/README.md)`n- [Web](../Web/README.md)

## Deployment telemetry update

Both SelfHosted and SaaS use the private Telemetry disk gateway for new deployment logs and metrics. PostgreSQL payload
writes are rejected at application startup; legacy reads and draining of existing outbox rows remain available. The
gateway confirms durable checksummed writes before cloud checkpoints advance. Tenant-scoped v2 history, deployment
revision recovery and weighted daily project analytics are documented in [the rollout guide](/docs/saas-telemetry.md).
Detailed data expires after 30 days; daily statistics after 365 days. See the root navigation.md for new module entry
points.
