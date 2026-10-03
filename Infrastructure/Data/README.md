# Data

EF Core DbContext, mappings, token protection conversion, and migrations.
SaaS cloud runs, transactional outbox wakeups, webhook receipts, and installation cooldowns are stored in PostgreSQL.
Run configuration snapshots use a separate Data Protection purpose because they may contain customer environment
secrets; SaaS startup requires a shared certificate to encrypt the database key ring.

## Source inventory

- `AutoMateDbContext.cs`
- `Migrations/` — EF Core schema history, including GitHub workflow/job streaming checkpoints.

GitHub workflow checkpoints retain only state fingerprints, line counts, and redacted-content hashes needed to resume
diagnostic streaming; they never store raw GitHub Actions log content.
Deployment diagnostic records retain a PostgreSQL identity ordering cursor and terminal channel for authorized replay.
The retention service removes expired records; operators must review backup lifecycle against the 30-day data policy,
encrypt backups, restrict restore access, and purge expired diagnostics before a restored database is exposed.

## Boundary

Infrastructure may reference Application and Domain, but never Web. It implements ports and owns provider-specific
behavior.

## Related documentation

- [Solution navigation map](../../.agents/navigation.md)

`AddSpecializedTelemetryStorage` adds explicit runtime/managed-egress preferences, tenant leases, durable rate/series
state and diagnostic outbox fields. `AddTelemetryBufferAccounting` backfills tenant/global buffer byte accounting.
Confirmed specialized payloads leave PostgreSQL; legacy logs expire normally.
See [telemetry operations](../../docs/deployment-telemetry.md).
