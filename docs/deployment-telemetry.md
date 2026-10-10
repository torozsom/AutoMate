# Deployment telemetry storage

## Architecture and compatibility

PostgreSQL owns projects, deployment metadata, configuration references, provider checkpoints and cloud admission.
Both SelfHosted and SaaS require `TelemetryStorage:Backend=LokiMimir` and `DeliveryMode=DiskGateway`. The private
Telemetry service persists new raw events on disk; PostgreSQL remains the control plane and daily-summary store. Invalid
or missing gateway settings fail startup without raw database fallback. See [the rollout guide](saas-telemetry.md).
`LokiMimir`
sends
new terminal output to Loki and numeric CPU/memory observations to Mimir. Existing PostgreSQL logs remain readable until
their 30-day expiry. Never provision a database per deployment.

`DeploymentTelemetryStore` persists centrally redacted events before live delivery, including automatic runtime
observations while active, regardless of browser presence.
Legacy outbox records (created before DiskGateway rollout) reuse
`deployment_diagnostic_records` as a short-term outbox: immutable row ID, database order cursor, owner identity,
ingestion
time, safe event JSON, acceptance flag, byte accounting and buffer deadline. These are not another permanent 30-day
copy.
`telemetry_tenant_states` stores leases, retry deadlines, rate windows, bounded series identities and account loss
counts.
Provider acceptance alone does not delete payloads: all log IDs and exact raw metric samples must be query-visible.
Pending metric buckets take precedence over backend copies and are marked potentially incomplete until delivery settles.
The worker deletes only the complete earliest batch. Retries preserve identities, timestamps and values.

Loki uses service/source/severity labels and structured project/deployment/event metadata. Mimir receives OTLP/HTTP JSON
gauges and exposes Prometheus queries. Managed adapters require those protocols; a remote-write-only provider needs an
additional writer. Storage credentials and tenant headers are controlled by AutoMate, never supplied by browsers.

Numeric CPU uses cores: Docker percent / 100 (possibly greater than one), or Azure Monitor `UsageNanoCores` / 1 billion.
Local live metric cards receive each centrally redacted Docker stats observation independently of the persisted
60-second sample cadence. Slow or unavailable storage does not pause those live values. Durable sampled values are
not rebroadcast over fresher live observations; reload still restores the most recent saved snapshot.
Azure `WorkingSetBytes` and Docker memory use
bytes. Names and units are allow-listed; values must be finite and nonnegative. Legacy string-only metric rows expire
under the original policy and are not interpreted as typed observations. Queries return average/minimum/maximum without
zero-filling gaps, with at most 1,000 intervals per series and a 30-day range. PostgreSQL processing caps at 10,000
samples
and explicitly reports truncation rather than claiming completeness.

## Owner experience and APIs

Project terminals restore only Starting/Running deployments on reload and reconnect; Stopped/Failed deployments
initialize empty. Active replay merges specialized history, pending outbox
events and legacy PostgreSQL logs. Periodic catch-up recovers missed live output. Failed backend reads do not advance
the replay cursor; bounded rendered identities deduplicate buffered messages. Overflow produces an omission notice.

Deployment rows expose **View history**, opening `/project/{projectId}/deployment/{deploymentId}` with original terminal
channels, earlier cursor pages, CPU/memory charts and numeric sample tables. Metric charts default to retained 30-day
history, with 24-hour and 7-day zoom choices. Only one 500-event page remains in the
component. Metadata survives expiration. History reports pending, unavailable, expired/absent or incomplete data.

Authenticated APIs authorize project ownership and deployment membership before any provider query:

- `GET /api/projects/{projectId}/deployments/{deploymentId}/logs?cursor=0&backwards=true&limit=500`
- `GET /api/projects/{projectId}/deployments/{deploymentId}/metrics?start=<UTC>&end=<UTC>&maximumPoints=500`

Metric responses contain `points` and an optional `availability` notice. Neither API accepts arbitrary LogQL/PromQL.
Build/deployment and runtime diagnostics are automatic in both hosting profiles, including with every browser closed.
Docker and Azure collect the current active deployment without viewer or per-project preference gates. Sampling defaults
to 60 seconds. Managed processing still requires an operator-approved region and DPA; AI egress remains separately
consented and budgeted. Legacy preference APIs authorize ownership and retain collection/storage enabled even when older
clients submit false. There is no PostgreSQL payload fallback. Existing history and volume contents are preserved.

## Compact self-managed pilot

For a local dashboard and log-search UI, enable the optional
[Grafana override](../deploy/telemetry/README.md). It provisions fixed-tenant Loki/Mimir data sources and a deployment
dashboard, with persistent Grafana settings and a loopback-only port. Grafana reads confirmed store data directly,
while AutoMate history also merges pending buffers and legacy PostgreSQL records.

The optional [Compose profile](../deploy/telemetry/compose.yaml) provisions single-instance Loki, Mimir, SeaweedFS
S3-compatible storage and an authenticated TLS Nginx gateway. It persists working volumes and separate log/metric
buckets,
plus Mimir's system rule/alert buckets. Loki and Mimir run as UID 10001; a network-isolated initializer sets
working-volume
ownership. Only the gateway is published, on loopback. This pilot has no high-availability guarantee.

Generate private **development** credentials and a localhost TLS certificate using PowerShell 7:

```powershell
./deploy/telemetry/Initialize-TelemetrySecrets.ps1
docker compose --env-file .telemetry/pilot/compose.env -f deploy/telemetry/compose.yaml --profile telemetry up -d
```

The script writes to the ignored `.telemetry/pilot` directory, restricts directory access and refuses to overwrite an
existing configuration. Trust `tls.crt` on the AutoMate host or replace it with an organization-issued certificate/key.
Do not disable certificate validation. Load `.telemetry/pilot/automate.env` into AutoMate's environment or equivalent
.NET user secrets. That file selects LokiMimir, configures TLS endpoints and supplies a private gateway authorization
header. Never commit, print or give it to browsers.

For local IDE and `dotnet run` launches, run `./deploy/telemetry/Configure-TelemetryDevelopment.ps1` to import those
complete settings into Web user secrets, then restart AutoMate. Setting only the backend name omits required endpoints
and fails startup validation. The importer preserves unrelated user secrets and sets `CaCertificatePath` to the pilot's
public certificate. This scopes private-Cu trust to the telemetry HTTP client while retaining hostname verification;
it does not change system-wide trust. Omit that setting for endpoints already trusted by the host.

Apply `AddSpecializedTelemetryStorage` and `AddTelemetryBufferAccounting` before using the schema; normal startup
migration handles both. Provision with
`Backend=Postgres`, then enable LokiMimir for a controlled audience and restart AutoMate. Drain specialized buffers
before
switching back to PostgreSQL. Returning to LokiMimir resumes outstanding leases. No historical backfill or permanent
dual-writing occurs.

Public rollout requires encrypted host disks/object storage and backups, bucket-scoped service credentials, private TLS
between hosts, capacity monitoring and a recovery drill. Compose's internal S3 connections use plaintext on an isolated
Docker network, and Docker volumes do not themselves establish encryption. The compact bootstrap identity has broad
object-store rights; replace it with bucket-scoped identities before public rollout. These operator controls are not
automatically established by Compose. SeaweedFS replaces unavailable MinIO community container images.

## Quotas and retention

| Operator setting          | Default                                                    |
|---------------------------|------------------------------------------------------------|
| `BufferHours`             | 24 hours                                                   |
| `TenantBufferBytes`       | 128 MiB                                                    |
| `GlobalBufferBytes`       | 2 GiB                                                      |
| `TenantBytesPerMinute`    | 8 MiB                                                      |
| `MaximumMetricContainers` | 100 deployment/container identities within retention       |
| `BatchSize`               | 100                                                        |
| `QueryConcurrency`        | 4 active requests; at most 16 admitted callers per process |
| `RuntimeSampleSeconds`    | 60 seconds                                                 |

Admission uses a short PostgreSQL transaction/advisory lock; provider I/O runs outside it. Byte budgets include safe
payloads, duplicate row fields and row overhead, not PostgreSQL's entire index/WuL disk allocation. Rate windows survive
delivery. Mimir caps active series at 300 per owner; adjust this alongside AutoMate's container limit. Worker operations
time out after 45 seconds, before two-minute leases expire. Expired buffers are cleaned even while provider retries
pause.
Admission rejection and expiry update durable account loss counts and history notices. These conservative notices are
account-wide, not exact per-deployment loss counts.

Export `AutoMate.TelemetryStorage` measurements for admitted bytes, dropped events, retries, provider throttles, request
latency, spool size, overdue spool cleanup and visibility delay. The worker emits a bounded warning when payload cleanup
is overdue by ten minutes. The pilot's global ingestion lock and serial delivery cycle favor simple finite admission and
fairness over
maximum throughput. Measure database contention and eligible queue age before raising quotas or distributing services.

Queryable history expires at 30 days. Loki compacts every 10 minutes with a two-hour asynchronous delete delay. Mimir
compacts every 30 minutes with 30-day retention and a two-hour delete delay. ADR 0001 permits a monitored **48-hour
physical
cleanup grace**, including object-store reclamation. Never extend query retention to mask delayed cleanup.

Before public rollout, operators must:

1. Alert on drops, sustained visibility delay/retries and spool growth; alert if pending payloads remain more than ten
   minutes beyond their deadline.
2. Scrape compactor metrics privately and alert when cleanup stops or deletion backlog approaches 48 hours. Audit
   bucket objects, deletion markers and SeaweedFS vacuum/reclamation. An empty query alone does not prove physical
   deletion.
3. Govern object versions, snapshots and encrypted backups under the same deadline; purge expired data before exposing
   restored services. Any longer recoverable copy needs a retention exception.
4. Run an accelerated retention/recovery drill in an isolated stack, including compactor failure. A short test cannot
   establish a real 30-day wall-clock cleanup cycle.

## Tests

Run `dotnet test AutoMate.slnx` for credential-free tests. Real-store tests are skipped unless
`AUTOMATE_TELEMETRY_TEST_DB` explicitly targets the disposable PostgreSQL service in `compose.integration.yaml`. That
override publishes loopback-only test ports; never use it for public hosting or point tests at an application database.
Set `TEST_POSTGRES_PuSSWORD` in an ignored env file, use a distinct Compose project name, and remove only its test
volumes.

Tests cover actual migrations, redaction, replay before/after buffer deletion, numeric queries, authorization,
concurrent
byte admission, runtime opt-out, lease recovery, delayed visibility and expiry. Load measurements are pilot smoke tests,
not sustained production capacity claims. Managed services need a separate retention and data-processing review.
