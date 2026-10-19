# Authorized live project streams

SignalR LogHub and typed browser callback contract for terminal output and numeric observations.

## Contents

| File                           | Types or asset | Responsibility                                                                     |
|--------------------------------|----------------|------------------------------------------------------------------------------------|
| [ILogClient.cs](ILogClient.cs) | `ILogClient`   | SignalR client interface for receiving real-time logs and metrics from the server. |
| [LogHub.cs](LogHub.cs)         | `LogHub`       | Authorizes project log subscriptions and manages project-specific SignalR groups.  |

## Usage and integration

LogHub maps at /loghub. The authenticated Blazor presenter mints a short-lived protected subscription token;
JoinProjectGroup validates it, resolves project ownership/deployment membership, joins the connection group and returns
bounded redacted history. ILogClient defines stable terminal/notice/legacy metrics plus additive typed metric callbacks.

## Contracts and lifecycle

Group membership belongs to a connection: reconnect must authorize/join again and merge buffered live data by durable
order/event identity. Historical subscriptions do not start collection. Latest-deployment joins renew expiring viewer
interest, but runtime collection remains automatic with browsers closed.

Transport buffers are 64 KiB each direction; live sends have cancellable deadlines. Disconnect/navigation cancellation
ends replay without confirming a failed read cursor; provider cancellation on an active connection remains a failure.
Tokens, connection IDs and payloads stay out of custom trace fields.

## Related documentation

- [Parent module](../README.md)
- [Server sends](../Services/RealTimeLogStreamer.cs)
- [Join/replay consumer](../Components/Pages/Projects/README.md)
- [Viewer contract](../../Application/Abstractions/Diagnostics/Delivery/README.md)
- [Cancellation tests](../../Test/Web/Diagnostics/LogHubCancellationTests.cs)
- [Replay tests](../../Test/Web/Projects/ProjectTerminalReplayTests.cs)
