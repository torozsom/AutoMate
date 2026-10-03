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
- `DeploymentTerminalLog.cs` — persisted terminal message and bounded history contracts with a database order cursor.

## Related documentation

- [Solution navigation map](../../../.agents/navigation.md)

`DeploymentTelemetry.cs` defines separate log/metric write/query ports, typed numeric samples, bounded history responses
and owner-authorized preferences. History availability and cursor-advance flags distinguish delayed storage and
omissions.

`IDeploymentRuntimeViewers` tracks short-lived authorized collection interest. Output collected during viewing is
saved for replay; unattended collection requires the owner's separate background preference.
