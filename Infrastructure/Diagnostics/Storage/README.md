# Telemetry storage adapters

Private disk spool, Loki/Mimir access and legacy PostgreSQL history/drain compatibility.

## Contents

| File                                                                               | Types or asset                                                                      | Responsibility                                                                                    |
|------------------------------------------------------------------------------------|-------------------------------------------------------------------------------------|---------------------------------------------------------------------------------------------------|
| [DeploymentDiagnosticRetentionService.cs](DeploymentDiagnosticRetentionService.cs) | `DeploymentDiagnosticRetentionService`                                              | Deletes expired redacted diagnostics in bounded batches.                                          |
| [DeploymentDiagnosticStore.cs](DeploymentDiagnosticStore.cs)                       | `DeploymentDiagnosticStore`                                                         | Reads and expires legacy PostgreSQL diagnostics; new payload writes are prohibited.               |
| [DeploymentTelemetryStore.cs](DeploymentTelemetryStore.cs)                         | `DeploymentTelemetryStore`                                                          | Persists through the disk gateway and merges Loki history with legacy records for replay.         |
| [DiskTelemetrySpool.cs](DiskTelemetrySpool.cs)                                     | `DiskSpoolOptions`, `DiskTelemetrySpool`, `PendingWrite`, `Segment`, `PendingIndex` | Spool options, bounded write queue, checksummed segments and durable receipts.                    |
| [LokiDeploymentLogs.cs](LokiDeploymentLogs.cs)                                     | `LokiDeploymentLogs`                                                                | Loki JSON ingestion and bounded structured-metadata queries.                                      |
| [MimirDeploymentMetrics.cs](MimirDeploymentMetrics.cs)                             | `MimirDeploymentMetrics`                                                            | OTLP/HTTP numeric ingestion and Prometheus-compatible historical queries.                         |
| [TelemetryWriteBoundary.cs](TelemetryWriteBoundary.cs)                             | `TelemetryWriteBoundary`                                                            | Snapshots and redacts store-bound batches before serialization, including direct adapter callers. |

## Usage and integration

DiskTelemetrySpool owns bounded single-writer acceptance, checksummed segments, receipts and pending history.
LokiDeploymentLogs handles JSON log writes/queries; MimirDeploymentMetrics handles numeric OTLP writes and historical
queries. TelemetryWriteBoundary snapshots/redacts direct batches. DeploymentTelemetryStore merges current/legacy replay;
DeploymentDiagnosticStore and RetentionService read/expire old database rows.

## Contracts and lifecycle

Current new payloads go through DiskGateway; direct legacy PostgreSQL PersistAsync rejects writes. Raw option defaults
do not authorize fallback. Spool receipts require durable completion; backlog reads revalidate checksums/redaction.
Temporary delivery segments expire/bound usage independently of permanent archives.

WriteBoundary accepts bounded single-tenant batches and validates finite categories before serialization, even for
direct adapter callers. Backend labels/metric names/units stay finite; payload correlation is not an external metric
dimension. ContainsAsync confirms query visibility before delivery removal.

Replay merges legacy/pending/backend/archive data under stable order/event identities and current terminal masking
without rewriting historical records. Filter channels before paging; preserve gaps/availability. Context compatibility
reads remain bounded, not new AI snapshots. Backend query ports require already-authorized trusted scope.

## Related documentation

- [Parent module](../README.md)
- [Forwarding/confirmation](../Delivery/README.md)
- [Permanent history](../Archive/README.md)
- [Limits/options](../Policy/README.md)
- [Spool durability tests](../../../Test/Infra/Diagnostics/Archive/DiskTelemetrySpoolTests.cs)
- [Storage tests](../../../Test/Infra/Diagnostics/PolicyAndMetrics/TelemetryStorageTests.cs)
- [Replay tests](../../../Test/Infra/Diagnostics/Archive/DeploymentDiagnosticReplayTests.cs)

Spool startup reads clock, loss and series metadata asynchronously with the startup token. Recovery ordering and
durable segment/receipt semantics remain unchanged. Construction-only spool options retain configuration binding.
