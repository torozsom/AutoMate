# Hubs

SignalR hub and strongly typed browser callback contract for project-scoped streams. The client must authorize and join
its project group again after a SignalR reconnect because group membership belongs to a connection, not a user.
Joining requires a short-lived protected token minted in the authenticated Blazor circuit, project ownership, and a
deployment
belonging to that project. The join returns bounded redacted history after subscription, allowing clients to merge
buffered live events by database ordering cursor without gaps or duplicates.

An authorized join for the latest deployment also renews a 45-second live runtime viewing lease. Project details
renews through its existing catch-up handshake. Leaving/disconnecting removes interest; abandoned connections expire.
Historical subscriptions do not enable collection. Runtime output is automatically collected and saved for replay while
active, including with every browser closed.

Connection transport buffers are capped at 64 KiB in each direction. Live sends have cancellable deadlines; one slow
connection cannot accumulate unbounded writes or indefinitely occupy the diagnostic dispatcher. Reconnect continues to
use bounded persisted history and event identity deduplication.

If a reload or navigation disconnects during replay, the hub ends the canceled read without reporting a failed
subscription or confirming a replay cursor. Provider cancellation on an active connection remains an error.

## Source inventory

- `ILogClient.cs`
- `LogHub.cs`

## Boundary

Keep presentation behavior here. Components, hubs, routes, and services use Application contracts; concrete
Infrastructure types are registered only in Web/Configs.

## Related documentation

- [Solution navigation map](../../.agents/navigation.md)

Join/leave/replay emit correlated child spans. Replay disconnects are Canceled/Unset, active-provider cancellation is
Failed/Error and invalid tokens/access are Denied/Error. Tokens, identities, connection IDs and payloads are excluded.
The .NET server source provides invocation parents;
see [SignalR diagnostics](https://learn.microsoft.com/en-us/aspnet/core/signalr/diagnostics?view=aspnetcore-10.0).
