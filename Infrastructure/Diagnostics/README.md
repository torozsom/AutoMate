# Deployment Diagnostics

Infrastructure owns the diagnostic redactor and bounded in-memory delivery pipeline. It receives normalized events from
provider adapters, redacts them before every sink, adds structured logs/traces/metrics, and asynchronously forwards safe
terminal data through the Application logging port.

The pipeline intentionally does not persist diagnostic events in this milestone. Persistence and LLM context building
will be added only after retention and data-egress decisions are approved.

## Source inventory

- `DiagnosticRedactor.cs` — credential-pattern and sensitive-attribute masking.
- `DeploymentDiagnosticPipeline.cs` — bounded publisher and hosted dispatcher.

## Boundary

Infrastructure may depend on Application contracts and uses `ILogStreamer` only as a redacted presentation sink. It must
never make Web, SignalR, or provider payload types part of the diagnostic contract.

## Related documentation

- [Solution navigation map](../../.agents/navigation.md)
