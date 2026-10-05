# Deployment Diagnostics Port

This module owns the provider-neutral, versioned event contract for deployment observations and the ports that accept
them. Docker, GitHub, Azure, and other collectors normalize provider payloads here before delivery.

## Boundary

The contract is independent of OpenTelemetry, persistence, SignalR, and UI. Implementations must redact an event before
it reaches application logging, telemetry export, buffering, or any presentation transport. `ILogStreamer` remains a
separate UI delivery port and is not a diagnostic persistence or analysis API.

## Source inventory

- `DeploymentDiagnosticEvent.cs` — source, typed source identity/stream, kind, severity, correlation, terminal
  channel, and ordering contract.
- `IDeploymentDiagnosticPublisher.cs` — non-blocking ingestion boundary.
- `IDiagnosticRedactor.cs` — safe-copy redaction boundary.
- `IDeploymentDiagnosticStore.cs` — redacted persistence and bounded context boundary shared by replay and analysis.
- `DeploymentTerminalLog.cs` — persisted terminal message and bounded history contracts with a durable order cursor.

## Related documentation

- [Solution navigation map](../../../.agents/navigation.md)

`DeploymentTelemetry.cs` defines separate log/metric write/query ports, typed numeric samples, bounded history responses
and owner-authorized preferences. History availability and cursor-advance flags distinguish delayed storage and
omissions.

`IDeploymentRuntimeViewers` tracks short-lived authorized collection interest. Output collected during viewing is
saved for replay; unattended collection requires the owner's separate background preference.

## Deployment telemetry update

Both SelfHosted and SaaS use the private Telemetry disk gateway for new deployment logs and metrics. PostgreSQL payload
writes are rejected at application startup; legacy reads and draining of existing outbox rows remain available. The
gateway confirms durable checksummed writes before cloud checkpoints advance. Tenant-scoped v2 history, deployment
revision recovery and weighted daily project analytics are documented in [the rollout guide](/docs/saas-telemetry.md).
Detailed data expires after 30 days; daily statistics after 365 days. See the root navigation.md for new module entry
points.

`DeploymentTerminalLog` carries additive optional stream, severity, timestamp, source-instance and source-cursor
metadata.
`FromEvent` preserves these fields for both live delivery and disk/Loki replay; legacy messages remain compatible.
Presentation prefixes stderr without modifying the saved message or its replay identity.

## M5 shared text and terminal policy

`IDiagnosticRedactor.RedactText` masks standalone text with a caller-selected bounded limit. `DiagnosticRedaction`
applies it to terminal message/channel/instance/cursor fields while preserving GUIDs, ordering and event identity.
History and SignalR use the same projection policy; safe callback shapes and channel names remain unchanged.

Terminal projections add optional TraceId, SpanId and Sequence for in-memory AI evidence. FromEvent retains actual
source
metadata; RedactTerminal accepts only canonical hexadecimal trace identity. Legacy read projections retain their stored
time/severity/sequence without assigning invented event GUIDs. The analysis worker now uses
IDeploymentAnalysisContextBuilder; BuildContextAsync remains a compatibility text API without grounded evidence
semantics.
