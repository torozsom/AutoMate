# Diagnostic dispatch and transport

Bounded redacted publishing, durable gateway acceptance, safe live delivery and retried backend forwarding.

## Contents

| File                                                                 | Types or asset                                                                                      | Responsibility                                                                                   |
|----------------------------------------------------------------------|-----------------------------------------------------------------------------------------------------|--------------------------------------------------------------------------------------------------|
| [DeploymentDiagnosticPipeline.cs](DeploymentDiagnosticPipeline.cs)   | `DeploymentDiagnosticPublisher`, `DeploymentDiagnosticDispatcher`, `TelemetryTags`                  | Bounded redacted publisher, durable acceptance, hosted dispatcher and finite metric labels.      |
| [DiskTelemetryDeliveryWorker.cs](DiskTelemetryDeliveryWorker.cs)     | `DiskTelemetryDeliveryWorker`                                                                       | Delivery never holds a database transaction or falls back to a database payload queue.           |
| [TelemetryDeliveryWorker.cs](TelemetryDeliveryWorker.cs)             | `TelemetryDeliveryWorker`                                                                           | Leases tenant streams and retains each ordered batch until its complete prefix is query-visible. |
| [TelemetryGatewayClient.cs](TelemetryGatewayClient.cs)               | `TelemetryGatewayClient`                                                                            | Only server-side callers possess the private ingestion credential.                               |
| [TelemetryGatewayCompatibility.cs](TelemetryGatewayCompatibility.cs) | `TelemetryCompatibilityFailure`, `TelemetryCompatibilityException`, `TelemetryGatewayCompatibility` | Finite compatibility failures, safe guidance and bounded authenticated capability checks.        |
| [TelemetryHttpTransport.cs](TelemetryHttpTransport.cs)               | `TelemetryHttpTransport`, `TelemetryProviderException`                                              | Bounds authenticated store requests without exposing response bodies or gateway credentials.     |

## Usage and integration

DeploymentDiagnosticPublisher admits safe detached events into a bounded channel and offers PublishDurablyAsync for
cursor-sensitive collectors. Dispatcher persists before terminal delivery. TelemetryGatewayClient implements private
ingestion/archive calls; Compatibility probes required version/operations; HttpTransport bounds authenticated I/O.
DiskTelemetryDeliveryWorker drains disk segments; TelemetryDeliveryWorker handles legacy PostgreSQL buffered rows.

## Contracts and lifecycle

Fire-and-forget PublishAsync is not durable confirmation. Durable acceptance precedes cursor advancement, and a
slow/failed live sink cannot revoke confirmed storage. Overflow/loss produces explicit bounded notices; local live
metric observations are not rebroadcast from slower sampled history.

Backend delivery retains an ordered batch/segment until required log/metric reads confirm query visibility. Failures
retry with bounded scheduling; expiry/ownership changes record loss. Do not hold database transactions across provider
I/O or create new PostgreSQL payload fallback.

Transport deadlines cover capacity waits, headers and body; actual pending writes/reads observe cancellation. Private
credentials, URLs, bodies and exceptions never enter ordinary export fields. Capability failures distinguish
incompatible host, denied access and temporary unavailable; required channel/assessment/metric operations must not be
silently ignored.

## Related documentation

- [Parent module](../README.md)
- [Ports](../../../Application/Abstractions/Diagnostics/Delivery/README.md)
- [Spool/backend adapters](../Storage/README.md)
- [Live sink](../../../Application/Abstractions/Logging/README.md)
- [Isolation tests](../../../Test/Infra/Diagnostics/Delivery/DiagnosticDeliveryTests.cs)
- [Compatibility tests](../../../Test/Infra/Diagnostics/Delivery/TelemetryGatewayCompatibilityTests.cs)
- [Delivery tests](../../../Test/Infra/Diagnostics/Delivery/TelemetryIntegrationTests.cs)
