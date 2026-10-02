# Deployment Diagnostics

Infrastructure owns the diagnostic redactor, redacted diagnostic store, and bounded delivery pipeline. It receives
normalized events from provider adapters, redacts them before every sink, adds structured logs/traces/metrics, and fans
out safe terminal data after durable persistence.

The pipeline persists only already-redacted events before broadcasting them. Its dispatcher maps typed terminal channels
to stable UI channels, keeping GitHub Actions, Azure console, Azure system, local build, and local container output
separate. LLM egress
remains disabled until a separately approved provider, region, consent, and data-processing policy exists.
Blank log lines are valid terminal output; state and annotation messages must contain non-whitespace text. Untrusted
terminal escape sequences and unsafe control characters are removed before storage or delivery.
The publisher bounds its in-memory queue and each message to 4,096 characters; overflow produces a durable gap marker.
PostgreSQL
assigns a unique ordering cursor, and the store returns at most 500 recent terminal events for a deployment. A hosted
worker deletes expired records in batches every hour, including at startup.
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
