# Telemetry host

Private ASP.NET Core host for durable deployment diagnostics in SelfHosted and SaaS installations. It owns the
single-writer spool/archive volume, delivers redacted observations to Loki/Mimir and maintains daily metric summaries.
Web replicas call this process over authenticated HTTP and do not mount its telemetry volume.

## Contents

| File                                               | Responsibility                                                                                                      |
|----------------------------------------------------|---------------------------------------------------------------------------------------------------------------------|
| [Program.cs](Program.cs)                           | Builds the host through `TelemetryApplication.Build(args)` and runs it.                                             |
| [TelemetryApplication.cs](TelemetryApplication.cs) | `TelemetryApplication`: service composition, safe logging, bearer authentication and private routes.                |
| [Telemetry.csproj](Telemetry.csproj)               | .NET 10 Web SDK project; references Infrastructure and EF Core snake-case naming support.                           |
| [Dockerfile](Dockerfile)                           | Builds from the solution root; publishes the host and runs the ASP.NET runtime image as UID/GID 10001 on port 8080. |
| [Dockerfile.dockerignore](Dockerfile.dockerignore) | Limits the Docker context to the four required projects, excluding generated outputs and environment files.         |

This is the project's only maintained source folder. Generated `bin` and `obj` directories are excluded from the
folder documentation map.

## Composition and module boundaries

`TelemetryApplication.Build(string[] args, Action<WebApplicationBuilder>? configure = null)` returns an **unstarted**
application. The optional callback runs after production registrations and before construction; tests use it to replace
background dependencies. The caller owns startup, shutdown and disposal. Option validation and exclusive volume
ownership happen at startup; construction and startup failures propagate to the caller.

The project directly references [Infrastructure](../Infrastructure/README.md). Application contracts and Domain models
are available through that dependency; provider and persistence implementation stays outside this composition root.

| Concern                     | Implementation and consumers                                                                                                                                                                                                                      |
|-----------------------------|---------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------|
| Event and history contracts | [Application diagnostics](../Application/Abstractions/Diagnostics/README.md) defines the HTTP payloads, archive and metric ports.                                                                                                                 |
| Web-to-host transport       | [TelemetryGatewayClient](../Infrastructure/Diagnostics/Delivery/TelemetryGatewayClient.cs) sends observations; [TelemetryGatewayCompatibility](../Infrastructure/Diagnostics/Delivery/TelemetryGatewayCompatibility.cs) verifies archive support. |
| Durable ingestion           | [DiskTelemetrySpool](../Infrastructure/Diagnostics/Storage/DiskTelemetrySpool.cs), also declaring `DiskSpoolOptions`, owns bounded admission, receipts and replay.                                                                                |
| Permanent history           | [Archive module](../Infrastructure/Diagnostics/Archive/README.md) supplies `DiskDeploymentArchive`, metric batch reads, backfill and cleanup workers.                                                                                             |
| Backend delivery            | [Delivery module](../Infrastructure/Diagnostics/Delivery/README.md) supplies `DiskTelemetryDeliveryWorker` and HTTP transport; [Storage](../Infrastructure/Diagnostics/Storage/README.md) supplies Loki/Mimir adapters.                           |
| Ownership and configuration | [Policy module](../Infrastructure/Diagnostics/Policy/README.md) supplies bounded database-backed ownership caches and storage options.                                                                                                            |
| Daily summaries             | [Metrics module](../Infrastructure/Diagnostics/Metrics/README.md) supplies `TelemetryDailyAggregationWorker`.                                                                                                                                     |
| Metadata persistence        | [AutoMateDbContext](../Infrastructure/Data/AutoMateDbContext.cs) reads ownership/cleanup metadata and writes daily summaries.                                                                                                                     |
| Platform logs               | [Observability](../Infrastructure/Observability/README.md) supplies `AddSafePlatformLogging` before the host is built.                                                                                                                            |

## Private HTTP interface

Every route, including status and capabilities, requires `Authorization: Bearer <server credential>`. Authentication
compares SHA-256 hashes in fixed time and returns 401 for a missing/incorrect credential. Kestrel limits request bodies
to 1 MiB. The middleware preserves `TelemetryProviderException` status codes without forwarding provider error text.
Ownership comes from database relationships, never an incoming tenant header.

| Method and path                                | Behavior and important bounds                                                                                                                                                                                                                                                             |
|------------------------------------------------|-------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------|
| `GET /status`                                  | Bounded queue status: events, charged bytes, segments, oldest age and persistent loss count.                                                                                                                                                                                              |
| `GET /capabilities`                            | Protocol version and supported archive operation names, without tenant evidence or credentials.                                                                                                                                                                                           |
| `POST /ingest`                                 | Validates identifiers/enums, message/channel sizes, timestamps and supported finite nonnegative metrics; resolves project/deployment ownership, redacts again and returns a durable receipt. Ordinary ingestion accepts timestamps from the preceding 30 days through five minutes ahead. |
| `GET /pending/{tenant}/{project}/{deployment}` | Checks ownership and returns bounded pending history with truncation metadata for authorized history merging.                                                                                                                                                                             |
| `GET /archive/{tenant}/{project}/{deployment}` | Checks ownership; defaults to a backward page of 500 log records. Optional cursor, direction, limit, search and channel select the page; channel filtering precedes the bounded read.                                                                                                     |
| `POST /archive/assessment`                     | Checks ownership and reads an immutable assessment selection with absolute UTC bounds; invalid archive arguments return 400.                                                                                                                                                              |
| `POST /archive/assessment-metrics`             | Checks ownership and selected metric evidence; requires a positive window of at most 365 days.                                                                                                                                                                                            |
| `POST /archive/metric-batch`                   | Resolves authorized partitions in SQL; reads ten partitions per continuation and at most 4,000 aggregates. Private UTC boundary fragments may be shorter than the public five-minute minimum; the range may span up to five calendar years. Partial-output metadata must be preserved.    |
| `POST /archive/metrics`                        | Checks ownership, positive bounds and a requested maximum of 1–1,000 points.                                                                                                                                                                                                              |
| `POST /archive/import`                         | Imports historical log envelopes with positive ordering and stable identity; validates payloads and ownership separately from ordinary ingestion's 30-day lower bound.                                                                                                                    |
| `POST /archive/import-metrics`                 | Checks ownership and imports at most 3,000 points per request.                                                                                                                                                                                                                            |
| `POST /archive/delete`                         | Deletes project history only after the application project is absent and an owner-matching durable cleanup request exists.                                                                                                                                                                |

Request/response types are linked from the [delivery](../Application/Abstractions/Diagnostics/Delivery/README.md),
[history](../Application/Abstractions/Diagnostics/History/README.md) and
[metrics](../Application/Abstractions/Diagnostics/Metrics/README.md) contract inventories. Exact validation and status
handling live in [TelemetryApplication.cs](TelemetryApplication.cs) and the delegated archive implementations.

## Configuration and startup

Apply the application's [EF migrations](../Infrastructure/Migrations/README.md) through the Web deployment before
starting this host. Telemetry configures PostgreSQL access but **does not apply migrations**.

| Setting                                                                                | Requirement                                                                                                                                                 |
|----------------------------------------------------------------------------------------|-------------------------------------------------------------------------------------------------------------------------------------------------------------|
| `ConnectionStrings:DefaultConnection`                                                  | Application metadata database, already migrated.                                                                                                            |
| `TelemetryStorage:Backend` / `DeliveryMode`                                            | Explicitly select `LokiMimir` / `DiskGateway`; legacy defaults do not satisfy this host's startup validation.                                               |
| `TelemetryStorage:GatewayUrl` / `GatewayToken`                                         | Private gateway URL and server-only credential of at least 32 characters; provision a random secret through environment/secret storage.                     |
| `TelemetryStorage:LokiUrl`, `MetricsWriteUrl`, `MetricsQueryUrl`                       | Complete backend endpoint configuration. Production endpoints require HTTPS without embedded credentials.                                                   |
| `TelemetryStorage:CaCertificatePath`                                                   | Optional existing public CA certificate for the named telemetry HTTP client's custom trust store.                                                           |
| `TelemetryStorage:ManagedService`, `ManagedDataProcessingApproved`, `ProcessingRegion` | Managed storage requires explicit operator processing approval and a disclosed region. These are operator settings, not per-project collection preferences. |
| `DiskSpool:Directory`                                                                  | Absolute path on an encrypted persistent volume; provisioning encryption, permissions and backups is an operator responsibility.                            |
| `DiskSpool:BatchSize`                                                                  | Write-batch maximum; defaults to 100.                                                                                                                       |

[TelemetryStorageOptions](../Infrastructure/Diagnostics/Policy/TelemetryStorageOptions.cs) defines validated resource
bounds: the buffer age defaults to 24 hours, tenant/global byte budgets to 128 MiB/2 GiB and tenant ingestion to
8 MiB per rolling minute. `AllowInsecureDevelopment` permits HTTP only for non-managed development configuration;
it does not provision transport security. Never expose the private credential to browsers or logs.

From the repository root, with the required configuration supplied:

```powershell
dotnet run --project Telemetry/Telemetry.csproj
docker build -f Telemetry/Dockerfile -t automate-telemetry .
```

The [application Compose definition](../deploy/app/compose.yaml) wires the private networks, non-root volume permissions
and persistent volume. Consult [deployment guidance](../deploy/README.md) and the
[telemetry stack guide](../deploy/telemetry/README.md) before running it.

## Persistence, lifecycle and operational limits

Run **one active host per volume**. Startup acquires an exclusive spool lock and rejects a second writer. The production
composition requires both permanent archive and spool persistence before acknowledging ingestion. Versioned segment
files contain redacted payloads and SHA-256 checksums; pending indexes stay in memory. Separate state preserves clocks,
metric identity budgets and losses. Incomplete unacknowledged temporary segments can be discarded; acknowledged segment
corruption fails startup closed and preserves the file for recovery. Do not manually remove files from a running spool.
Disk loss is outside the receipt guarantee; encryption and recovery are deployment responsibilities.

The shared singleton redactor snapshots/masks payloads and the separate channel before spool admission. Replay verifies
checksums, then applies the current masking policy without rewriting the stored backlog. Byte/rate/metric identity
quotas bound acceptance. Backend delivery retries stable identities and waits for query visibility; expiry sweeping
continues during backend outages. A lost receipt can cause duplicate delivery: history deduplicates event IDs, while
loss/error totals remain conservative or approximate. New payloads have no raw PostgreSQL fallback.

The delivery buffer's bounded outage lifetime is separate from permanent history. Archive history and daily summaries
are retained until project deletion and cleanup; deployment status changes do not delete that history. Derived metric
indexes/day manifests are rebuildable from checksummed archive segments. Hourly aggregation revisits the current and
previous two UTC dates for running or recently updated deployments and replaces daily observations; missing evidence
is not zero-filled and pre-collection history is not invented.

Ownership caches are bounded and expire after five seconds. Collection and persistence are automatic; legacy project
preference flags do not gate ingestion, delivery or archive access. Database ownership checks and managed-provider
operator approval still apply. The host installs safe platform logging before ordinary providers; it registers no
OpenTelemetry SDK exporter. Future exporters require equivalent processor, view and resource-policy review.

Monitor rising loss counts, persistent delivery failures, restarts and oldest pending age approaching the configured
buffer limit. See [storage operations](../docs/deployment-telemetry.md) and the
[permanent history decision](../docs/adr/0004-permanent-deployment-history.md) for recovery and retention context.

## Compatibility and verification

[TelemetryCapabilities](../Application/Abstractions/Diagnostics/Delivery/TelemetryCapabilities.cs) currently advertises
protocol version 1 with `logs`, `channel-logs`, `metrics`, `metric-batch`, `assessment`, `assessment-metrics`, `import`,
`import-metrics` and `delete`. Deploy this matching host before updated Web/workers and preserve the existing volume;
Web verifies the required operation set. Filtering must happen before limits, and consumers must retain continuation,
truncation and availability metadata.

| Tests                                                                                                          | Coverage                                                                                                                                                                                                            |
|----------------------------------------------------------------------------------------------------------------|---------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------|
| [TelemetryHostSafetyTests](../Test/Web/Hosting/TelemetryHostSafetyTests.cs)                                    | Production composition over loopback with background services removed; missing credentials, capabilities, malformed authenticated JSON and safe provider output. Does not exercise external stores or real workers. |
| [TelemetryGatewayCompatibilityTests](../Test/Infra/Diagnostics/Delivery/TelemetryGatewayCompatibilityTests.cs) | Required capability negotiation and incompatible gateway handling.                                                                                                                                                  |
| [DiskTelemetrySpoolTests](../Test/Infra/Diagnostics/Archive/DiskTelemetrySpoolTests.cs)                        | Durable spool behavior, replay, admission, redaction and recovery regressions.                                                                                                                                      |
| [DiskDeploymentArchiveTests](../Test/Infra/Diagnostics/Archive/DiskDeploymentArchiveTests.cs)                  | Permanent archive reads, filtering, metric evidence and retained history.                                                                                                                                           |

```powershell
dotnet build Telemetry/Telemetry.csproj --no-restore
dotnet test Test/Test.csproj --filter FullyQualifiedName~Test.Web.Hosting.TelemetryHostSafetyTests
```

## Related documentation

- [Solution navigation](../navigation.md) and [Web host](../Web/README.md)
- [Infrastructure diagnostics](../Infrastructure/Diagnostics/README.md)
  and [test map](../Test/Infra/Diagnostics/README.md)
- [SaaS telemetry rollout](../docs/saas-telemetry.md)
- [Assessment filtering](../docs/status-aware-assessments.md) and [metric exploration](../docs/metric-exploration.md)

Handwritten types/members use multiline XML documentation; top-level startup statements and anonymous route handlers
use ordinary comments. Keep host details here and storage/provider behavior in the linked implementation guides.
