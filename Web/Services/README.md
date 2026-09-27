# Services

`Web/Services` contains presentation-host adapters that connect framework
transport APIs to service-layer contracts. The current module has one service:
`RealTimeLogStreamer`.

It adapts the framework-managed SignalR hub context to the
`Services.LogStreaming.ILogStreamer` abstraction. This keeps Docker, Azure,
GitHub workflow, and orchestration code independent from SignalR and Blazor.

## Structure

```text
Web/Services/
└── RealTimeLogStreamer.cs
```

The service is registered by `Web/Configs/ServiceConfiguration.cs`:

```csharp
services.AddScoped<ILogStreamer, RealTimeLogStreamer>();
```

## `RealTimeLogStreamer`

`RealTimeLogStreamer` implements `ILogStreamer` using:

```csharp
IHubContext<LogHub, ILogClient>
```

It publishes messages to the project-specific SignalR group without knowing
which browser connections are currently subscribed.

### Build logs

`StreamBuildLogsAsync(Guid projectId, string message)`:

1. validates that `projectId` is not empty;
2. derives the group name through `LogHub.GetProjectGroupName`;
3. invokes `ILogClient.ReceiveBuildLog` for that group.

Build messages may originate from local Docker builds, cloud preparation,
GitHub Actions monitoring, or other orchestration stages. The adapter does
not add prefixes, timestamps, severity parsing, or persistence.

### Container logs

`StreamContainerLogsAsync(Guid projectId, string containerName, string message)`:

1. validates the project ID;
2. rejects a blank container identifier;
3. resolves the project group;
4. invokes `ReceiveContainerLog(containerName, message)`.

The container identifier is a stream/UI key. `ProjectDetails` currently routes
`web` to the web terminal and other recognized identifiers to database
terminals. The adapter must forward the identifier unchanged.

### Container metrics

`StreamContainerMetricsAsync(Guid projectId, string containerName,
string cpuUsage, string memoryUsage)`:

1. validates the project ID;
2. rejects a blank container identifier;
3. resolves the project group;
4. invokes `ReceiveContainerMetrics`.

CPU and memory values remain strings because producers currently provide
display-oriented values. Parsing and metric-bar rendering belong to the
component layer, not this transport adapter.

## Grouping and Authorization

The group convention is owned by `Web.Hubs.LogHub`:

```text
project-{projectId}
```

`RealTimeLogStreamer` always calls `LogHub.GetProjectGroupName` instead of
constructing the string itself. This must remain aligned with:

- `LogHub.JoinProjectGroup`;
- `LogHub.LeaveProjectGroup`;
- `ProjectDetails` connection setup;
- all log-streaming producers.

The adapter does not authorize project access for every message. Authorization
occurs when a SignalR client joins a group: `LogHub` validates a short-lived
Data Protection token and confirms the user owns the requested application.

Only trusted server-side producers should receive `ILogStreamer`. Do not use
this class as a browser-facing authorization API or expose arbitrary group
publishing to clients.

## Data Flow

```text
Docker / Azure / GitHub workflow / orchestration
    -> Services.LogStreaming.ILogStreamer
    -> RealTimeLogStreamer
    -> IHubContext<LogHub, ILogClient>
    -> project-{projectId} group
    -> ProjectDetails SignalR handler
    -> Terminal or metrics state
```

The service is transport-only:

- it does not read process, Docker, Azure, or GitHub output;
- it does not create or manage SignalR connections;
- it does not buffer or replay missed messages;
- it does not persist logs or metrics;
- it does not parse provider-specific payloads;
- it does not create project groups.

## Validation and Errors

The adapter throws `ArgumentException` for:

- `Guid.Empty` project IDs;
- blank container identifiers for container events.

This prevents malformed group names and ambiguous container routing. It does
not validate message contents or reject blank build/log payloads because the
underlying stream contract permits producers to decide what output to send.

SignalR publish failures are allowed to propagate to the calling producer.
Callers decide whether a transient client/disconnect failure should be logged,
retried, or treated as part of deployment failure. Do not add broad catches or
silent success fallbacks here.

All methods are asynchronous and should preserve cancellation and failure
behavior from the underlying hub context. The current `ILogStreamer` contract
does not carry a cancellation token; do not invent one only in this adapter
without updating the shared contract and every producer.

## Dependency Injection and Lifetime

The service is scoped because it is injected into scoped application services
and receives a framework-managed `IHubContext`. It has no mutable per-project
state and does not retain connections.

`IHubContext<LogHub, ILogClient>` is safe for publishing from background
deployment work. The hub context targets all currently connected members of
the selected group; it does not require an active hub instance.

Do not change this service to singleton or add scoped dependencies without
reviewing hosted-service scope boundaries. Background workers that publish
through `ILogStreamer` must obtain the service through their existing
application scope pattern.

## Client Contract Boundary

The adapter uses the strongly typed `ILogClient` methods:

| Stream method | SignalR callback |
|---|---|
| `StreamBuildLogsAsync` | `ReceiveBuildLog` |
| `StreamContainerLogsAsync` | `ReceiveContainerLog` |
| `StreamContainerMetricsAsync` | `ReceiveContainerMetrics` |

If a new event is needed:

1. add a semantic method to `Services.LogStreaming.ILogStreamer`;
2. add the matching method to `Web.Hubs.ILogClient`;
3. implement it here;
4. add the client-side handler;
5. keep it scoped to the existing project group;
6. update producer and module documentation.

Do not add provider-specific callbacks such as `ReceiveDockerStats` or
`ReceiveAzureLogs`. The Web transport should expose UI-semantic messages,
while provider-specific work remains in `Services`.

## Security and Privacy

- Treat log output as potentially sensitive.
- Do not log complete messages, tokens, connection strings, or environment
  values from this adapter.
- Preserve project-group scoping for every event.
- Never accept a group name directly from a caller.
- Keep authorization in `LogHub` and ownership checks in the application
  service.
- Do not send secrets through a new client callback.
- Review terminal escape sequences and untrusted provider output before
  displaying it in xterm.js.

The adapter is intentionally unaware of user identity. Adding identity lookup
here would duplicate the hub's join authorization and make background
publishing dependent on request context.

## Testing Guidance

Unit tests should verify:

- empty project IDs throw for all stream methods;
- blank container identifiers throw for container logs and metrics;
- build logs call `ReceiveBuildLog`;
- container logs preserve container name and message;
- metrics preserve container, CPU, and memory values;
- every call targets `project-{projectId}`;
- failures from `IHubContext`/client proxies are not silently swallowed.

Integration tests should cover:

- local Docker log publishing;
- cloud runtime and workflow publishing;
- a joined project receiving only its own events;
- no delivery to an unrelated project group;
- publishing while clients connect, disconnect, or reconnect;
- behavior when no clients are currently connected.

## Extending the Module

When adding another Web service adapter:

1. identify the framework or transport contract it bridges;
2. keep the shared abstraction in `Services` when it is infrastructure
   independent;
3. implement only Web-specific transport behavior here;
4. register it in `Web/Configs/ServiceConfiguration.cs`;
5. preserve an explicit lifetime and scope compatibility;
6. validate identifiers at the boundary;
7. pass cancellation and failures through instead of hiding them;
8. update this README and the owning Services module documentation.

Do not move Docker, Azure, GitHub, database, or deployment business logic into
this folder. If an adapter needs provider-specific decisions, place them in
the corresponding Services module and expose only the necessary contract.

## File Map

| File | Purpose |
|---|---|
| `RealTimeLogStreamer.cs` | Converts `ILogStreamer` events into strongly typed SignalR group messages. |

## Related Documentation

- [`Web`](../README.md)
- [`Web/Hubs`](../Hubs/README.md)
- [`Web/Configs`](../Configs/README.md)
- [`Web/Components`](../Components/README.md)
- [`Services/LogStreaming`](../../Services/LogStreaming/README.md)
- [`Services/Orchestration`](../../Services/Orchestration/README.md)
- [`Services/Docker`](../../Services/Docker/README.md)
- [`Services/Azure`](../../Services/Azure/README.md)
