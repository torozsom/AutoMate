# Hubs

SignalR hub and strongly typed browser callback contract for project-scoped streams. The client must authorize and join
its project group again after a SignalR reconnect because group membership belongs to a connection, not a user.

## Source inventory

- `ILogClient.cs`
- `LogHub.cs`

## Boundary

Keep presentation behavior here. Components, hubs, routes, and services use Application contracts; concrete
Infrastructure types are registered only in Web/Configs.

## Related documentation

- [Solution navigation map](../../.agents/navigation.md)
