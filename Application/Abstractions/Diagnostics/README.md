# Deployment Diagnostics Port

This module owns the provider-neutral, versioned event contract for deployment observations and the ports that accept
them. Docker, GitHub, Azure, and other collectors normalize provider payloads here before delivery.

## Boundary

The contract is independent of OpenTelemetry, persistence, SignalR, and UI. Implementations must redact an event before
it reaches application logging, telemetry export, buffering, or any presentation transport. `ILogStreamer` remains a
separate UI delivery port and is not a diagnostic persistence or analysis API.

## Source inventory

- `DeploymentDiagnosticEvent.cs` — source, kind, severity, correlation, terminal channel, and ordering contract.
- `IDeploymentDiagnosticPublisher.cs` — non-blocking ingestion boundary.
- `IDiagnosticRedactor.cs` — safe-copy redaction boundary.

## Related documentation

- [Solution navigation map](../../../.agents/navigation.md)
