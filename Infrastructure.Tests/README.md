# Infrastructure Tests

Automated, credential-free verification for deployment diagnostics and external-adapter normalization.

Run from the repository root:

```powershell
dotnet test AutoMate.slnx --no-restore
```

These tests use fake HTTP responses and temporary SQLite databases. They do not contact Docker, GitHub, Azure, or an
OpenTelemetry collector.

Telemetry integration tests additionally use an explicitly configured disposable PostgreSQL/Loki/Mimir stack. They are
skipped without `AUTOMATE_TELEMETRY_TEST_DB`. See [telemetry verification](../docs/deployment-telemetry.md).

## Deployment telemetry update

Both SelfHosted and SaaS use the private Telemetry disk gateway for new deployment logs and metrics. PostgreSQL payload
writes are rejected at application startup; legacy reads and draining of existing outbox rows remain available. The
gateway confirms durable checksummed writes before cloud checkpoints advance. Tenant-scoped v2 history, deployment
revision recovery and weighted daily project analytics are documented in [the rollout guide](/docs/saas-telemetry.md).
Detailed data expires after 30 days; daily statistics after 365 days. See the root navigation.md for new module entry
points.
