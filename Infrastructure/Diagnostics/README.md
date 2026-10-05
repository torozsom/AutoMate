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
`DeploymentDiagnostics` controls `BufferCapacity` (512 by default), `PersistenceTimeoutSeconds` (10) and
`DeliveryTimeoutSeconds` (2). Startup validates their finite ranges. Storage, live output and availability notices
propagate cancellation to their actual sink operations. A storage timeout leaves durable source checkpoints unchanged;
a live timeout leaves saved history available for replay and does not terminate the dispatcher. SignalR bounds each
connection's inbound/outbound transport buffers to 64 KiB. Overflow bookkeeping is capped at 4,096 project identities;
omissions beyond that cap still increment dropped-event metrics. Gap markers have separate event identities.
Both queued and durable ingestion attach trace context before redaction. Dispatch restores that context for its child
activity. Queue depth, ingestion/sink latency, delivery freshness, duplicate suppression and collector error/recovery
metrics use only finite source/kind/channel/operation labels. Deployment and owner IDs are never metric labels.
Legacy PostgreSQL records use database ordering cursors; new DiskGateway records use durable spool positions and
independent event IDs, and normal terminal replay returns 500 recent events for a deployment. The PostgreSQL
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

`DeploymentTelemetryStore` writes new payloads through the disk gateway and merges legacy PostgreSQL reads with Loki
history. `LokiDeploymentLogs` and
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

Disk/Loki terminal projections and live dispatcher delivery both use `DeploymentTerminalLog.FromEvent`, preserving
stdout/stderr, severity, timestamp, instance and cursor metadata for presentation and local replay checkpoint recovery.
The stored redacted message stays unchanged; the Web presentation helper adds the visible stderr marker. No new
PostgreSQL log/metric writer or fallback is introduced by local Docker diagnostics.

## Platform log correlation

Durable ingestion and background dispatch carry GUID-only deployment/project ILogger scopes. Storage/delivery/notice
failure logs expose the failure type without attaching raw exceptions, which may contain provider or database payloads.
Successful dispatch never copies terminal messages into platform logs, including legacy delivery modes. Redaction is
pattern-based, so arbitrary private text must remain confined to authorized tenant storage and presentation.
Redaction already emits only counts and finite source/kind values. New platform audit events never duplicate diagnostic
payloads; existing disk gateway and Loki/Mimir storage paths remain unchanged. Real SDK failure exports are tested in
`Web.Tests/OperationalLoggingTests.cs`; Web/Observability now enforces the platform logging/trace boundary.

Redaction, persistence confirmation and live delivery have fixed child spans under the ingestion/dispatch trace, with
GUID correlation and safe outcomes. Checkpoints and stored event trace/span IDs keep their existing semantics. Custom
spans never record diagnostic payloads.

## M5 masking boundaries

The central redactor recognizes quoted JSON/environment/connection-string secrets, authorization schemes, cookies,
PEM private keys (including unterminated blocks), URI user information, JWTs and known GitHub/OpenAI token formats.
Sensitive structured keys are normalized across separators; credential values embedded in keys are masked too.
Source/routing/cursor and metric name/unit strings are bounded and masked; invalid trace/span IDs are dropped.
Control sequences are removed before matching, so they cannot split a credential name. Every regex has a finite
100 ms deadline; fields over 128 Ki characters or matching failures produce a fixed redaction marker. Ordinary terminal
output is capped at 4,096 characters including its omission marker; blank lines and progress carriage returns survive.
Repeated redaction is idempotent. This pattern policy does not identify arbitrary unlabeled secrets or complete the
remaining terminal PII and provider/context policy.

The disk gateway rechecks event and channel text before persistence. `DeploymentTelemetryStore` additionally applies
the current terminal policy to merged legacy, pending and Loki history on read without rewriting historical records
or changing cursor semantics. Context is redacted and bounded again before returning through the diagnostic port.
No new PostgreSQL payload writer/fallback is added. Web/Observability separately sanitizes platform logging and trace
exports. Web also applies metric views and a four-field resource allowlist; details and extension constraints are in
Web/Observability/README.md. Universal protection for arbitrary unsupported integrations is not claimed.

## Diagnostic queue/cache audit (M5)

| Boundary                                               | Allowed content and existing protection                                                                                                                                                                                                                                                              |
|--------------------------------------------------------|------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------|
| DeploymentDiagnosticPublisher channel                  | Bounded detached redacted events before admission; finite metric dimensions only.                                                                                                                                                                                                                    |
| DiskTelemetrySpool channel/segments                    | Mandatory injected shared redactor snapshots event attributes/metrics and channel before queue admission. Current masking also applies after checksum verification on backlog read, without rewriting acknowledged files. Identity, receipt, cancellation and durable flush semantics are preserved. |
| Deployment analysis work rows                          | Analysis IDs and claim/completion metadata only; context is loaded just before invocation and is never cached or persisted in these rows. Durable leases/retries remain M6 work.                                                                                                                     |
| TelemetryProjectPolicyCache / TelemetryAdmissionPolicy | Bounded five-second GUID ownership/consent projections; no diagnostic payloads, provider errors, credentials or context.                                                                                                                                                                             |
| GitHub repository distributed cache                    | Repository DTO metadata for the existing repository UI; token-hashed keys and ten-minute TTL, no workflow diagnostic logs or AI context. Repository metadata is private operational data, not an approved external telemetry dimension.                                                              |
| In-process DeploymentJobQueue                          | Bounded execution inputs may contain credentials required for deployment; inputs remain local, are never diagnostic context/metric labels, and are not serialized to distributed cache. Redacting those inputs would break existing deployment operation.                                            |
| SaaS cloud queue/outbox                                | Run IDs and scheduling metadata; token-free configuration snapshot is protected using the existing EF data-protection converter. Verified webhook receipts store selected workflow metadata rather than raw HTTP payloads/logs.                                                                      |

New diagnostic payloads stay in the private disk spool and Loki/Mimir. Both Web profiles reject PostgreSQL payload
modes;
legacy PostgreSQL implementations remain for compatibility reads/draining. This audit covers the supported diagnostic
and
analysis paths, not a blanket policy to redact operational authentication/configuration inputs. New queues/caches must
explicitly declare which role they serve; never add a diagnostic/context snapshot to metadata or operational entries.
The private Telemetry host registers the stateless redactor as singleton so the singleton spool receives the same
policy.

The M5 analysis context reader consumes the existing diagnostic history and Mimir ports. Legacy terminal projections now
retain actual stored timestamp/severity/sequence/trace metadata for selection/evidence; specialized history retains
those fields from its event envelope. New payload persistence, history authorization/cursors and disk-spool behavior are
unchanged. No context snapshots or analysis diagnostic payloads are written to PostgreSQL. See ../Ai/README.md.
