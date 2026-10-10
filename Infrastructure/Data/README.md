# Data

`20261005190054_AddAiTenantBudgets` creates the metadata-only `ai_analysis_budget_entries` table with owner/date/kind,
rolling-time and unique lease indexes. Only the owner FK cascades, so project/result deletion preserves usage. Retained
quota-consuming request receipts are backfilled as admissions; historical provider cost is not inferred. Generated SQL
and EF model consistency are reviewed; applying the migration requires the normal stopped-worker rollout workflow.
The full migration chain and retained-receipt backfill now execute in disposable PostgreSQL regression schemas. The
budget join uses the actual `cs_projects.app_id` FK; no application/production migration is applied by verification.

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

## Deployment telemetry update

Both SelfHosted and SaaS use the private Telemetry disk gateway for new deployment logs and metrics. PostgreSQL payload
writes are rejected at application startup; legacy reads and draining of existing outbox rows remain available. The
gateway confirms durable checksummed writes before cloud checkpoints advance. Tenant-scoped v2 history, deployment
revision recovery and weighted daily project analytics are documented in [the rollout guide](/docs/saas-telemetry.md).
Detailed data expires after 30 days; daily statistics after 365 days. See the root navigation.md for new module entry
points.

`20261004201350_AddAiAnalysisResultProvenance` adds nullable provenance/usage/cost fields to AiDeploymentAnalysis only.
Cost precision is numeric (18,8); provider/model/prompt identifiers are bounded at 100 characters and currency at three.
Existing legacy results are not backfilled with invented provenance. The generated SQL alters no telemetry tables.

20261005091947_AddAnalysisQueueLeases adds attempt_count, lease_id and lease_until only to analysis work metadata,
with an eligibility index on completed_at/lease_until/created_at. Legacy null leases are recoverable. Stop older workers
before applying the migration and starting lease-aware binaries. SQL was reviewed; this change does not apply it or
alter log/metric storage.

20261005094754_AddAnalysisRetryScheduling adds nullable next_attempt_at and default-zero provider_retry_count to
analysis work metadata and replaces its eligibility index. No log/metric table changes or context snapshots are added.
SQL is reviewed and the EF model matches; the migration is not applied. Stop older workers before applying/starting
retry-aware binaries because older queue implementations ignore future eligibility deadlines.

Owner cancellation uses existing analysis status/completion and queue completion/lease/deadline columns in one
transaction. Cancelled appends integer value 5 without renumbering existing states. EF reports no pending model change;
this cancellation slice introduces no migration and applies none of the previously prepared metadata migrations.

20261005102239_AddAnalysisAdmissionReceipts adds the ai_analysis_requests metadata table, unique request key and
quota/expiry indexes. Its only foreign key cascades with the project, not result/deployment deletion. PostgreSQL SQL
backfills surviving analysis admissions from ninety days with explicit UTC dates and GUID-only legacy keys; deleted
history cannot be recovered. SQL/model consistency is reviewed; no migration is applied. Stop older Web/worker instances
before applying and starting these binaries because old admission code ignores project serialization/receipt accounting.

20261005104851_AddFailedDeploymentAnalysisEvents adds a GUID/timestamp-only outbox and PostgreSQL AFTER INSERT OR
UPDATE OF status trigger. It checks the actual old/new status, captures integer Failed=3, and uses ON CONFLICT on the
deployment primary key. Tracked saves, direct SQL and bulk startup cleanup therefore capture failures in the same
transaction. No existing failures are backfilled. Completed markers are retained until deployment cascade deletion;
result/receipt cleanup cannot create duplicate automatic admission. Down removes trigger/function before the table.
The migration is prepared and unapplied; no log/metric tables are altered. Apply prepared metadata migrations before
starting the new dispatcher. Do not use EnsureCreated for production: it cannot install this migration-owned trigger.

## Deployment history update (2026-10-08)

PreserveDeploymentHistory adds snapshots/outcomes, permanent AI-result markers and archive cleanup metadata. Positive
Running/Failed facts backfill outcomes; stopped-only records remain Unknown. A PostgreSQL project-deletion trigger
queues archive cleanup in the same transaction, including account cascades.

AutomaticTelemetryCollection backfills existing runtime/storage compatibility flags and adds true defaults. It preserves
all saved diagnostic/history rows; downgrade removes defaults without restoring historical opt-out values.
