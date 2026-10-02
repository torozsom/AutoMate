# Web Services

Web transport adapters that implement Application contracts. `RealTimeLogStreamer` receives only redacted diagnostic
terminal data from Infrastructure's hosted dispatcher and forwards its source-aware channel to project-authorized
SignalR groups.
Terminal messages include deployment ID and PostgreSQL order cursor; safe availability notices use a separate
callback when storage or delivery fails.

## Source inventory

- `RealTimeLogStreamer.cs`

## Boundary

Keep presentation behavior here. Components, hubs, routes, and services use Application contracts; concrete
Infrastructure types are registered only in Web/Configs.

## Related documentation

- [Solution navigation map](../../.agents/navigation.md)
