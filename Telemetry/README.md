# Deployment telemetry service (SelfHosted and SaaS)

This separate private ASP.NET Core process owns the single disk-spool writer, Loki/Mimir delivery and hourly daily
aggregation. Web replicas hold no telemetry disk volume. Application contracts are in
`Application/Abstractions/Diagnostics`; filesystem, provider and EF implementations remain in Infrastructure.

Run only one active instance against a spool volume. The exclusive volume lock rejects another writer. Ingestion
requires a server-only bearer credential (`TelemetryStorage:GatewayToken`, at least 32 random characters), a private
TLS endpoint and `DiskSpool:Directory` pointing to an encrypted persistent volume. No customer browser receives this
credential. Incoming tenant headers are ignored; a bounded five-second database policy cache determines ownership.
Apply Web's EF migrations before starting this process; this service never migrates the application database.

`POST /ingest` receives a normalized diagnostic with a stable event ID. It redacts again, validates metric names/units
and owner/deployment membership, then acknowledges only after immutable checksummed segment and clock state flushes.
Queue admission bounds bytes, bytes/minute and retained metric identities. Delivery waits for query visibility, retries
without changing payloads and sweeps expired segments independently of backend availability. No raw PostgreSQL fallback
exists. The persistent loss counter is conservative and account-wide. At-least-once retries can duplicate a previously
delivered event after a lost receipt; history deduplicates event IDs, while error totals remain explicitly approximate.

The spool keeps only pending-event indexes in memory. Versioned JSON segment files contain redacted payloads, a SHA-256
checksum and version; partial unacknowledged temporary segments are discarded, acknowledged corruption fails startup
closed and keeps the file for operator recovery. Clock, series budgets and loss counters are separate state files.
Disk loss is outside this pilot's guarantee. Do not manually remove files from a running spool.

Authenticated `GET /status` reports queued events, charged bytes, segment count, oldest pending age and loss count.
Alert on rising losses, age near the 24-hour limit, failed delivery and service restarts.
`GET /pending/{tenant}/{project}/{deployment}`
returns a bounded pending read for AutoMate's authorized history merge. The read limit is reported as truncation.

Hourly aggregation replaces deployment/day/container/metric rows, rereads the current and previous two UTC dates and
retains daily summaries for 365 days. It does not claim data before collection started or exact error counts.

See [SaaS telemetry rollout](../docs/saas-telemetry.md) and [navigation](../navigation.md).

The M5 central policy also masks the separate ingestion channel before spool persistence. Existing authentication,
owner admission, metric validation, event identity and durable confirmation are unchanged. Production encryption and
operational retention drills remain rollout work.

The spool itself now snapshots/redacts event payloads and channel before channel admission, including direct callers.
Backlog reads verify checksums first, then mask with the current policy for delivery/replay without rewriting files.
The shared stateless redactor is a singleton. Durable receipt, quotas and cancellation behavior remain unchanged.

## Host safety and composition

Program delegates to TelemetryApplication.Build, which contains the existing services, authentication and routes and
installs Infrastructure/Observability's AddSafePlatformLogging before building. Framework/EF/HTTP and worker logs cross
that factory before console/debug/custom providers. Unknown templates/categories, URLs/SQL, raw exceptions and arbitrary
scope values are omitted; reviewed delivery/spool/daily alerts retain fixed messages and finite metadata. There is no
SDK
exporter registration in this host. Future exporters require processor/view/resource review before enablement.

TelemetryApplication exposes optional builder customization for deployment/test hosting; normal startup passes only
args. Loopback tests use the production composition, replace background dependencies and retain auth/route/option
policy:
missing credentials return 401 and malformed authenticated JSON returns 400 without leaking values to captured
providers.
Loki/Mimir adapters apply mandatory final redaction even for direct calls. Legacy PostgreSQL diagnostic writers now
reject
new payloads at the method boundary; existing reads/draining remain available through Web.
