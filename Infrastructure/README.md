# Infrastructure

EF Core persistence, external-system adapters, and Application-port implementations.

## Source inventory

- `Infrastructure.csproj`

## Boundary

Infrastructure may reference Application and Domain, but never Web. It implements ports and owns provider-specific
behavior.

## Related documentation

- [Domain](../Domain/README.md)
- [Application](../Application/README.md)
- [Web](../Web/README.md)

## Deployment analysis

[Ai](Ai/README.md) implements Application ports with EF metadata admission, renewable leases, durable retries,
automatic wakeup dispatch, bounded retention, current-consent authorization, shared owner-account quotas/capacity/
cost reservations and provider-free readiness. The provider catalog routes approved direct OpenAI regional endpoints and
exact Azure OpenAI resource endpoints.
Both adapters share bounded Responses validation/redaction and cancel local I/O on policy reload. Each worker slot owns
a
fresh scope/context. No context payload is queued or persisted.
See [onboarding and ordered metadata migrations](../docs/ai-analysis.md)
and [execution/rollout ADR](../docs/adr/0003-ai-analysis-execution-and-rollout.md); provider approval remains pending.

## Deployment telemetry update

Both SelfHosted and SaaS use the private Telemetry disk gateway for new deployment logs and metrics. PostgreSQL payload
writes are rejected at application startup; legacy reads and draining of existing outbox rows remain available. The
gateway confirms durable checksummed writes before cloud checkpoints advance. Tenant-scoped v2 history, deployment
revision recovery and weighted daily project analytics are documented in [the rollout guide](/docs/saas-telemetry.md).
Operational detailed data expires after 30 days; permanent archives and daily summaries retain until project deletion.
See the root navigation.md for new module entry
points.

Infrastructure/Observability owns the shared SDK-independent logger factory, literal catalog and typed policy used by
Web and the private Telemetry host. SDK exporter processors/views remain in Web/Observability. Diagnostic adapters
recheck writes/readback, and legacy PostgreSQL diagnostic ingestion methods reject new payloads even when called
directly.

## Deployment history update (2026-10-08)

Private Telemetry archives deployment diagnostics on its persistent volume; Web queries through IDeploymentArchive. See
docs/adr/0004-permanent-deployment-history.md for migration and host ordering.
