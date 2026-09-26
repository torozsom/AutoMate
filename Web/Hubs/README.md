# Hubs

`Web/Hubs` is the SignalR transport boundary for project-scoped deployment
logs and container metrics. It defines the strongly typed client contract and
authorizes browser clients before they join a project's live stream group.

The module does not collect Docker/Azure output, run deployments, persist
messages, or render terminal UI. Producers publish through
`Services.LogStreaming.ILogStreamer`; `Web.Services.RealTimeLogStreamer`
adapts that contract to this hub.

## Structure

```text
Web/Hubs/
├── LogHub.cs
└── ILogClient.cs
```

The hub is mapped by `Web/Configs/AppConfiguration`:

```csharp
app.MapHub<LogHub>("/loghub");
```

`ProjectDetails` creates the SignalR client connection and subscribes to the
client methods defined by `ILogClient`.

## Responsibilities

- Define the `/loghub` SignalR transport.
- Validate short-lived project join tokens.
- Confirm that the token's user can access the requested project.
- Add and remove connections from project-specific groups.
- Define build-log, container-log, and container-metric client messages.
- Keep group naming consistent between publishers and subscribers.
- Pass connection cancellation to application-service and group operations.

The module does not:

- authenticate the user through the standard hub authorization attribute;
- trust a project ID by itself;
- issue deployment commands;
- read Docker or Azure streams;
- buffer, aggregate, or persist messages;
- parse metric values;
- decide which users own a project.

## Client Contract

`ILogClient` is the strongly typed SignalR client interface:

| Method | Parameters | UI destination |
|---|---|---|
| `ReceiveBuildLog` | `message` | Build terminal in `ProjectDetails`. |
| `ReceiveContainerLog` | `containerName`, `message` | Web or database terminal selected by container identifier. |
| `ReceiveContainerMetrics` | `containerName`, `cpuUsage`, `memoryUsage` | Container metrics panel. |

Metrics are strings by design. Docker and Azure producers currently publish
display-oriented values such as percentages and memory `used/limit` values.
Parsing and visualization remain in `ProjectDetails`; the hub forwards the
payload without normalization.

Messages are project-scoped through the group membership, not through a
project ID parameter on every client callback. Do not add an unscoped
broadcast method for deployment data.

## Project Groups

Groups use the deterministic name:

```text
project-{projectId}
```

`LogHub.GetProjectGroupName` is the single source of truth. It is used by:

- `LogHub` when adding/removing a connection;
- `RealTimeLogStreamer` when publishing messages;
- any future publisher that sends project-scoped events.

Do not duplicate the naming format in producers or components. A mismatch
silently produces a connected client with no messages.

## Join Authorization Flow

`LogHub` is marked `[AllowAnonymous]` because the hub connection itself is
not the authorization boundary. Project access is authorized explicitly when
the client calls `JoinProjectGroup`.

The current flow is:

```text
ProjectDetails resolves internal user ID
    -> creates Data Protection protector with purpose "LogHub"
    -> protects "{projectId}:{userId}" for 5 minutes
    -> connects to /loghub
    -> calls JoinProjectGroup(projectId, secureToken)
    -> LogHub unprotects and validates the payload
    -> IApplicationService.GetAppByIdAsync(projectId, userId)
    -> Groups.AddToGroupAsync
```

Join validation rejects the request when:

- the project ID is empty;
- the secure token is blank;
- the token cannot be unprotected;
- the payload does not contain exactly two parts;
- either part is not a GUID;
- the token project ID differs from the requested project ID;
- the application service cannot find a project owned by the token user.

Invalid or expired protected tokens are caught as `CryptographicException`
and logged at debug level. Invalid payloads are rejected without adding the
connection to any group. The method does not return a success-shaped result,
so callers should treat the absence of stream messages as a failed join and
must not assume membership from a successful hub invocation alone.

The protection purpose is shared with `ProjectDetails`:

```csharp
internal const string ProtectorPurpose = "LogHub";
```

Changes to the payload format, purpose, or token lifetime must be coordinated
between the hub and every client/token issuer.

## Token and Identity Rules

The protected payload contains only:

```text
projectId:userId
```

The token is time-limited to five minutes by the current page client. Data
Protection provides confidentiality and integrity for the payload; it is not a
replacement for the ownership lookup.

The ownership check through `IApplicationService.GetAppByIdAsync` is
mandatory. Do not authorize a group join solely because the token is
cryptographically valid: a valid token must still resolve to a user-owned
application.

Do not:

- place access tokens, passwords, connection strings, or log content in the
  protected payload;
- accept a user ID directly from browser claims or method parameters;
- log the secure token or its unprotected payload;
- extend the lifetime unnecessarily;
- expose a method that lets clients choose arbitrary group names.

## Leave and Disconnect Lifecycle

`LeaveProjectGroup` removes the current connection from the deterministic
project group when the client navigates away or disposes.

It ignores an empty project ID and passes `Context.ConnectionAborted` to
SignalR group removal. `ProjectDetails.DisposeAsync` calls it when the
connection is still connected, then disposes the `HubConnection`.

SignalR also removes disconnected connections from groups. The explicit leave
call is still important for predictable navigation cleanup and for avoiding
stale memberships during a still-open connection.

If a new client adds timers, subscriptions, or server-side connection state,
clean it up on disconnect or component disposal. Do not retain per-connection
state in the hub instance; hub instances are transient.

## Publishing Flow

The server-side adapter is `Web.Services.RealTimeLogStreamer`:

```text
Docker/Azure/orchestration producer
    -> ILogStreamer
    -> RealTimeLogStreamer
    -> IHubContext<LogHub, ILogClient>
    -> project-{projectId}
    -> ILogClient callback
    -> ProjectDetails terminal/metrics state
```

`RealTimeLogStreamer`:

- rejects empty project IDs;
- rejects empty container identifiers for container messages/metrics;
- derives the group through `LogHub.GetProjectGroupName`;
- invokes the matching strongly typed client method.

It does not perform project ownership checks for every message. Authorization
is established when a client joins the group, and only trusted server-side
producers should have access to the `ILogStreamer` abstraction.

The hub does not provide backpressure or persistence. Producers and clients
must tolerate transient disconnects, missed messages, and reconnection.

## Connection and Client Behavior

`ProjectDetails` creates a `HubConnection` on first render with automatic
reconnect enabled. It registers handlers before starting the connection:

- build logs write to the build `Terminal`;
- container logs route `web` to the web terminal and other identifiers to
  database terminals;
- metrics update the container dictionary and available metric-container
  list on the Blazor synchronization context.

After `StartAsync`, the component sends `JoinProjectGroup`. On disposal it
leaves the group and disposes the connection.

The hub does not replay messages after reconnect. If replay or current-state
reconciliation is required, add an explicit server-side query/snapshot
contract rather than assuming SignalR will recover missed output.

## Error and Cancellation Behavior

- Empty IDs and blank tokens are ignored without group changes.
- Invalid protected tokens are rejected and logged at debug level.
- Ownership lookup uses `Context.ConnectionAborted`.
- Group add/remove uses `Context.ConnectionAborted`.
- Server-side publish calls fail through the `ILogStreamer` adapter when
  validation rejects malformed IDs or container names.
- Client-side connection/start failures are logged by `ProjectDetails`.
- Cancellation from disconnect should not be converted into an application
  error or retried indefinitely.

Avoid broad catches in hub methods. Only cryptographic rejection is currently
handled locally because invalid/expired join tokens are an expected client
condition. Unexpected application-service or SignalR failures should remain
visible to the hosting pipeline/logging.

## Security Considerations

- Keep the hub route and group names project-scoped.
- Preserve the ownership lookup before `Groups.AddToGroupAsync`.
- Keep token lifetime short and use the shared `LogHub` Data Protection
  purpose.
- Do not mark new hub methods anonymous unless they independently validate
  authorization.
- Do not send secrets or private configuration through log messages.
- Treat build output and runtime logs as potentially sensitive.
- Ensure upstream producers publish only to valid, intended project IDs.
- Review log injection and terminal escape-sequence behavior when accepting
  new message sources.
- Do not expose connection IDs, protected tokens, or internal exception
  details to clients.

`[AllowAnonymous]` on the hub is deliberate for the token-based project
authorization flow. Removing it would require aligning the standard
authentication cookie with SignalR transport negotiation and would not remove
the need for the project ownership check.

## Extending the Contract

To add a new streamed event:

1. Add a method to `ILogClient` with a stable, minimal payload.
2. Add the corresponding method to `ILogStreamer` if the event crosses the
   Services/Web boundary.
3. Implement the method in `RealTimeLogStreamer`.
4. Register a client handler in `ProjectDetails` or the owning component.
5. Keep the event project-scoped through the existing group.
6. Define validation for IDs, names, and sensitive values.
7. Pass cancellation to asynchronous operations.
8. Update tests and this README.

Do not add source-specific methods such as `ReceiveDockerStats` or
`ReceiveAzureLog` to the client contract. Use semantic UI events so Docker,
Azure, and future providers remain behind `ILogStreamer`.

If a new event needs authorization beyond project ownership, add an explicit
server-side check or a separate hub contract. Group membership alone is not a
substitute for a new authorization rule.

## Testing Guidance

Test `LogHub` with:

- empty project IDs and blank tokens;
- malformed, expired, and tampered tokens;
- payloads with incorrect part counts;
- project-ID mismatch;
- unknown or unauthorized project/user combinations;
- successful owned-project joins;
- valid leave operations and empty-ID leaves;
- cancellation during ownership lookup and group operations;
- expected debug logging for cryptographic rejection.

Test `RealTimeLogStreamer` with:

- empty project IDs;
- empty container identifiers;
- correct group names;
- correct strongly typed client method and payload;
- build, container-log, and metric publishing independently.

Integration tests should verify that:

- a user can receive only the project group they successfully joined;
- another user's project cannot be joined with a modified token;
- local and cloud producers reach the same client contract;
- reconnect/disposal does not leave stale application state;
- terminal routing and metric updates work for web and database containers.

## File Map

| File | Purpose |
|---|---|
| `LogHub.cs` | Project-group join/leave methods, protected-token validation, ownership lookup, and group naming. |
| `ILogClient.cs` | Strongly typed browser/client callbacks for build logs, container logs, and metrics. |

Related adapter:

| File | Purpose |
|---|---|
| `Web/Services/RealTimeLogStreamer.cs` | Publishes `ILogStreamer` events to project-specific SignalR groups. |

## Related Documentation

- [`Web`](../README.md)
- [`Web/Components`](../Components/README.md)
- [`Web/Configs`](../Configs/README.md)
- [`Services/LogStreaming`](../../Services/LogStreaming/README.md)
- [`Services/Data`](../../Services/Data/README.md)
- [`Services/Orchestration`](../../Services/Orchestration/README.md)
- [`Services/Docker`](../../Services/Docker/README.md)
- [`Services/Azure`](../../Services/Azure/README.md)
