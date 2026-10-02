# Hubs

SignalR hub and strongly typed browser callback contract for project-scoped streams. The client must authorize and join
its project group again after a SignalR reconnect because group membership belongs to a connection, not a user.
Joining requires a short-lived protected token minted in the authenticated Blazor circuit, project ownership, and a
deployment
belonging to that project. The join returns bounded redacted history after subscription, allowing clients to merge
buffered live events by database ordering cursor without gaps or duplicates.

## Source inventory

- `ILogClient.cs`
- `LogHub.cs`

## Boundary

Keep presentation behavior here. Components, hubs, routes, and services use Application contracts; concrete
Infrastructure types are registered only in Web/Configs.

## Related documentation

- [Solution navigation map](../../.agents/navigation.md)
