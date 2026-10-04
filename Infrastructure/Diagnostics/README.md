# Deployment Diagnostics

Infrastructure owns the diagnostic redactor, redacted diagnostic store, and bounded delivery pipeline. It receives
normalized events from provider adapters, redacts them before every sink, adds structured logs/traces/metrics, and fans
out safe terminal data after durable persistence. Runtime output collected during authorized viewing is saved for
replay too; the owner preference controls collection while no page is open.

The pipeline persists only already-redacted events before broadcasting them. Its dispatcher maps typed terminal channels
to stable UI channels, keeping GitHub Actions, Azure console, Azure system, local build, and local container output
separate. LLM egress
remains disabled until a separately approved provider, region, consent, and data-processing policy exists.
Blank log lines are valid terminal output; state and annotation messages must contain non-whitespace text. Untrusted
terminal escape sequences and unsafe control characters are removed before storage or delivery.
The publisher bounds its in-memory queue and each message to 4,096 characters; overflow produces a durable gap marker.
Legacy PostgreSQL records use database ordering cursors; new DiskGateway records use durable spool positions and independent event IDs, and normal terminal replay returns 500 recent events for a deployment. The PostgreSQL
fallback retention worker deletes expired records in batches every hour, including at startup; specialized stores use
their configured compactors, while the delivery worker removes confirmed or expired short-term payloads.
For records written before deployment correlation was added, replay also recognizes project-owned GitHub Actions and
Docker Compose output within the deployment's creation-time window and maps it to the appropriate terminal tab.

## Source inventory

- `DiagnosticRedactor.cs` — credential-pattern and sensitive-attribute masking.
- `DeploymentDiagnosticPipeline.cs` — bounded publisher and hosted dispatcher.
- `DeploymentDiagnosticStore.cs` — 30-day redacted diagnostic retention and bounded context construction.
- `DeploymentDiagnosticRetentionService.cs` — bounded deletion of expired records.

## Boundary

Infrastructure may depend on Application contracts and uses `ILogStreamer` only as a redacted presentation sink. It must
never make Web, SignalR, or provider payload types part of the diagnostic contract.

## Related documentation

- [Solution navigation map](../../.agents/navigation.md)

## Specialized storage

`DeploymentTelemetryStore` merges PostgreSQL fallback/outbox and Loki history. `LokiDeploymentLogs` and
`MimirDeploymentMetrics` implement separate log/metric ports. `TelemetryDeliveryWorker` leases tenant batches and waits
for complete-prefix query visibility. `DeploymentHistoryService` authorizes historical reads and consent changes.
`TelemetryStorageOptions` validates quotas/transport, and `TelemetryHttpTransport` bounds active and waiting requests.
See [hosting and retention operations](../../docs/deployment-telemetry.md).

`TelemetryStorageOptionsValidator` reports safe, setting-specific startup failures for missing endpoints and invalid
limits. It never includes configured endpoint values or credentials in validation messages.

`DeploymentRuntimeViewers` bounds authorized process-local viewing leases to 4,096 connections/project pairs and expires
them after 45 seconds without renewal. Collected runtime output uses durable positive cursors for replay.
Viewing alone does not enable unattended collection. Storage requests include capacity waits and body reads in a
10-second deadline; provider timeouts return an availability notice without terminating live delivery.

Local live numeric metrics are a presentation snapshot: `DockerMetricDelivery` centrally redacts each observation and
delivers it without waiting for persistence. Only sampled observations enter durable history (60 seconds by default).
The dispatcher persists those samples without rebroadcasting them over newer live values. Terminal logs retain
persist-before-delivery semantics.

## Deployment telemetry update

Both SelfHosted and SaaS use the private Telemetry disk gateway for new deployment logs and metrics. PostgreSQL payload
writes are rejected at application startup; legacy reads and draining of existing outbox rows remain available. The
gateway confirms durable checksummed writes before cloud checkpoints advance. Tenant-scoped v2 history, deployment
revision recovery and weighted daily project analytics are documented in [the rollout guide](/docs/saas-telemetry.md).
Detailed data expires after 30 days; daily statistics after 365 days. See the root navigation.md for new module entry
points.
