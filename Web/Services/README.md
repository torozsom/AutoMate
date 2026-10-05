# Web Services

Web transport adapters that implement Application contracts. `RealTimeLogStreamer` receives only redacted diagnostic
terminal data from Infrastructure's hosted dispatcher and forwards its source-aware channel to project-authorized
SignalR groups.
Terminal messages include deployment ID and durable order cursor/event identity; safe availability notices use a
separate
callback when storage or delivery fails.
`RealTimeLogStreamer` uses the cancellable SignalR client proxy with the existing `ILogClient` callback names and
argument shapes. `DeploymentDiagnostics:DeliveryTimeoutSeconds` defaults to 2 seconds for terminal logs, live metrics
and availability notices. Caller cancellation propagates; expiration cancels the write and reports a safe timeout.
Saved logs remain recoverable through the existing replay path. Hub transport buffers are 64 KiB per direction.

## Source inventory

- `RealTimeLogStreamer.cs`

## Boundary

Keep presentation behavior here. Components, hubs, routes, and services use Application contracts; concrete
Infrastructure types are registered only in Web/Configs.

## Related documentation

- [Solution navigation map](../../.agents/navigation.md)

SignalR writes emit fixed child spans with project GUIDs. Caller cancellation, deadline expiry and failures remain
separate. Existing callbacks, arguments, routing and exceptions are unchanged; payloads and names are not span tags.

The shared `IDiagnosticRedactor` is also enforced at the final SignalR write boundary for terminal text/metadata,
availability notices and live metric display strings. This protects legacy/bypassed callers as well as the normal
redacted pipeline. GUIDs, cursor/event identity, safe channels, callback names, cancellation and deadlines stay the
same.
