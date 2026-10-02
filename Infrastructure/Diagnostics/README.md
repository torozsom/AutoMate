# Deployment Diagnostics

Infrastructure owns the diagnostic redactor, redacted diagnostic store, and bounded delivery pipeline. It receives
normalized events from provider adapters, redacts them before every sink, adds structured logs/traces/metrics, and fans
out safe terminal data and replay/context persistence through independent bounded workers.

The pipeline persists only already-redacted events. Its dispatcher maps typed terminal channels to stable UI channels,
keeping GitHub Actions, Azure console, Azure system, local build, and local container output separate. LLM egress
remains disabled until a separately approved provider, region, consent, and data-processing policy exists.
Blank log lines are valid terminal output; state and annotation messages must contain non-whitespace text.

## Source inventory

- `DiagnosticRedactor.cs` — credential-pattern and sensitive-attribute masking.
- `DeploymentDiagnosticPipeline.cs` — bounded publisher and hosted dispatcher.
- `DeploymentDiagnosticStore.cs` — 30-day redacted diagnostic retention and bounded context construction.

## Boundary

Infrastructure may depend on Application contracts and uses `ILogStreamer` only as a redacted presentation sink. It must
never make Web, SignalR, or provider payload types part of the diagnostic contract.

## Related documentation

- [Solution navigation map](../../.agents/navigation.md)
