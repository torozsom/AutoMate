# Log Streaming

`Services/LogStreaming` defines the presentation-independent event contract
used to publish deployment output and runtime metrics. The module contains no
transport implementation; its consumers publish through `ILogStreamer`, and
`Web.Services.RealTimeLogStreamer` adapts those calls to SignalR.

This separation keeps Docker, Azure, and deployment orchestration independent
from SignalR, Blazor, connection IDs, and client component state.

## Responsibilities

- Define the shared `ILogStreamer` contract.
- Publish build/deployment log messages by project.
- Publish container log messages by project and container identifier.
- Publish CPU and memory display values by project and container identifier.
- Provide a stable boundary for both local Docker and cloud Azure runtime
  streams.

The module does not:

- read Docker or Azure output;
- decide when a deployment starts or stops;
- manage background workers or cancellation sources;
- authorize project access;
- create SignalR groups;
- render logs or metrics in the UI.

## Module Structure

```text
Services/LogStreaming/
└── ILogStreamer.cs
```

The current implementation is outside this project:

```text
Web/Services/RealTimeLogStreamer.cs
```

The SignalR client contract is also Web-owned:

```text
Web/Hubs/ILogClient.cs
Web/Hubs/LogHub.cs
```

## Public Contract

`ILogStreamer` exposes three asynchronous operations:

| Method | Payload | Destination meaning |
|---|---|---|
| `StreamBuildLogsAsync` | `projectId`, `message` | Build, Compose, cloud preparation, or workflow output. |
| `StreamContainerLogsAsync` | `projectId`, `containerName`, `message` | Runtime stdout/stderr associated with one container/tab. |
| `StreamContainerMetricsAsync` | `projectId`, `containerName`, `cpuUsage`, `memoryUsage` | Runtime CPU and memory display values. |

Every event is scoped by `projectId`. Container events add a string
identifier so the UI can route output to the web container or a database
container. The identifier is a display/stream key, not necessarily the
literal Docker container name:

- local Docker commonly uses `web` or a database suffix;
- Azure publishes the cloud runtime as `cloud-web`.

The contract intentionally carries metrics as strings because Docker and
Azure currently provide display-oriented values such as `0.42%` and
`128MiB / 1GiB`. Parsing and normalization remain in the source adapter.

## Event Flow

### Build logs

```text
Docker Compose stdout/stderr
    -> DockerCli
    -> ILogStreamer.StreamBuildLogsAsync
    -> RealTimeLogStreamer
    -> SignalR project group
    -> ProjectDetails.ReceiveBuildLog
```

Cloud workflow preparation and GitHub Actions logs use the same build-log
channel:

```text
CloudDeploymentOrchestrator / GitHubWorkflowMonitor
    -> ILogStreamer.StreamBuildLogsAsync
    -> SignalR project group
```

Callers may add a source prefix, such as `[cloud]`, before publishing. The
streaming contract does not interpret message content.

### Container logs

```text
Docker.DotNet or Azure runtime poller
    -> ILogStreamer.StreamContainerLogsAsync
    -> SignalR project group
    -> ProjectDetails.ReceiveContainerLog
```

Container output is forwarded as received or after source-specific decoding.
The log-streaming boundary does not add timestamps, parse levels, truncate
messages, or merge streams.

### Container metrics

```text
Docker stats or Azure Monitor/runtime data
    -> ILogStreamer.StreamContainerMetricsAsync
    -> SignalR project group
    -> ProjectDetails.ReceiveContainerMetrics
```

Metrics are point-in-time updates. The source worker controls polling
frequency; `ILogStreamer` does not buffer, aggregate, throttle, or persist
them.

## Current Web Adapter

`Web.Services.RealTimeLogStreamer` implements `ILogStreamer` using
`IHubContext<LogHub, ILogClient>`.

For every method it:

1. rejects an empty project ID;
2. rejects an empty container identifier for container events;
3. derives the project group name through `LogHub.GetProjectGroupName`;
4. invokes the corresponding strongly typed SignalR client method.

The group naming convention is:

```text
project-{projectId}
```

The implementation does not inspect authentication claims itself. Project
authorization occurs when a client joins the group through `LogHub`, which
validates a time-limited protected token and confirms ownership through
`IApplicationService`.

`ILogStreamer` therefore publishes only to an already-authorized project
group; it is not an authorization API.

## Dependency Injection and Lifetime

`Web.Configs.ServiceConfiguration` registers the adapter as scoped:

```csharp
services.AddScoped<ILogStreamer, RealTimeLogStreamer>();
```

The contract is injected into:

- `DockerCli`, for Compose/build/container output and Docker metrics;
- `GitHubWorkflowMonitor`, for cloud preparation and workflow logs;
- `AzureContainerAppRuntimeStreamer`, for cloud runtime availability and
  metrics;
- other orchestration helpers that need to publish deployment output.

Background stream managers create their own service scopes when they need
scoped Docker services. The log-streaming contract itself does not create
scopes, own workers, or retain state between calls.

## Producers

### Local Docker

`Services.Docker` publishes:

- Compose process stdout and stderr as build logs;
- container stdout/stderr as container logs;
- `docker stats` CPU and memory values as container metrics.

`LocalDeploymentLogStreamManager` starts separate log and metric tasks for the
web container and configured database containers. It owns per-project
cancellation and replaces an existing stream when a new stream starts.

The Docker module passes identifiers such as `web`, `postgres`, or another
configured database suffix. The log-streaming module must treat them as opaque
strings.

### Cloud deployment preparation

`Services.Orchestration.GitHubWorkflowMonitor` publishes:

- preparation messages prefixed with `[cloud]`;
- workflow status changes and URLs;
- flattened GitHub Actions logs after a run completes.

These are build-log events because they describe deployment preparation and
workflow execution rather than a live container's stdout.

### Azure runtime

`Services.Azure.AzureContainerAppRuntimeStreamer` polls Azure Container Apps
state and metrics after a successful cloud deployment. It publishes:

- an availability message when the ready revision or FQDN changes;
- CPU and memory metrics at the configured polling interval.

It uses the identifier `cloud-web` so the existing Project Details UI can
display cloud runtime data through the same container channels as local data.

## Consumer and Transport Boundaries

| Concern | Owner |
|---|---|
| Stream contract | `Services.LogStreaming.ILogStreamer` |
| Docker output acquisition | `Services.Docker` |
| GitHub workflow output acquisition | `Services.GitHub` and `Services.Orchestration` |
| Azure runtime polling | `Services.Azure` |
| Background stream lifecycle | `Services.Orchestration` and Azure runtime streamer |
| Project ownership verification | `Web.Hubs.LogHub` and `IApplicationService` |
| SignalR group publishing | `Web.Services.RealTimeLogStreamer` |
| SignalR client method names | `Web.Hubs.ILogClient` |
| UI rendering and buffering | `Web.Components.Pages.ProjectDetails` |

Do not reference SignalR, `IHubContext`, `Hub`, Blazor components, or browser
APIs from `Services/LogStreaming`. If another transport is needed, add an
implementation in the owning presentation/host project.

## Ordering, Delivery, and Backpressure

`ILogStreamer` is an asynchronous forwarding contract, not a durable message
queue. It does not guarantee:

- persistence after clients disconnect;
- replay for a newly joined client;
- cross-process ordering;
- delivery to clients that are not currently in the project group;
- buffering when the transport is unavailable.

Callers that need reliable deployment state must persist that state through
`Services.Data`; do not use streamed messages as the source of truth.

Within one producer, await the streaming call when the producer needs to
observe transport failure or preserve its own sequence. Do not introduce an
unbounded in-memory queue in this module without defining ownership,
shutdown, memory, and backpressure behavior.

## Validation and Error Behavior

The contract has no result value and no cancellation-token parameter. This
reflects the current SignalR adapter and caller patterns, where worker
cancellation is controlled by the producer and transport failures are handled
by the caller or hosting workflow.

Implementations should still:

- reject empty project IDs;
- reject empty container identifiers for container events;
- propagate meaningful transport failures rather than silently reporting
  success;
- avoid broad catches that hide disconnected clients or broken transport
  configuration;
- avoid logging complete messages when they may contain tokens, credentials,
  connection strings, or sensitive source output.

When changing the contract, consider adding cancellation only if all
producers and the Web adapter can honor it consistently. A signature change
affects Docker, Azure, orchestration, and any alternate transport
implementation.

## Security Rules

- Treat project IDs and container identifiers as untrusted input at the
  transport boundary.
- Do not use a caller-provided group name; derive the group from the validated
  project ID.
- Keep project authorization in `LogHub` before group membership is granted.
- Never publish access tokens, app passwords, refresh tokens, or secret values.
- Redact environment variables, connection strings, and sensitive command
  output before publishing if source tools can emit them.
- Do not expose raw SignalR connection IDs through this abstraction.
- Keep build and runtime messages scoped to the intended project.

The `RealTimeLogStreamer` implementation validates the project ID and
container identifier, while `LogHub` validates the protected join token and
owned application. Both boundaries are required; one must not be removed
because the other exists.

## Testing Guidance

Test the contract using a fake or spy `ILogStreamer` rather than starting
SignalR for every unit test. Verify that producers publish:

- the correct project ID;
- the correct build/container/metric channel;
- the expected container identifier;
- normalized source output and prefixes;
- cancellation and worker shutdown without new events afterward.

For the Web adapter, test:

- empty project IDs are rejected;
- empty container identifiers are rejected;
- the expected SignalR client method is invoked;
- the expected project group name is used;
- build, container-log, and metric payloads are forwarded unchanged.

Integration tests should separately cover `LogHub` token validation and
project ownership. Do not treat successful invocation of
`ILogStreamer` as proof that a client received or rendered the event.

## Extending the Module

When adding a new stream type:

1. Confirm it is transient presentation output rather than durable domain
   state.
2. Decide whether it belongs in an existing channel or requires a new
   contract method.
3. Define the project and secondary identifier semantics explicitly.
4. Update every producer and the Web adapter together.
5. Add a corresponding typed SignalR client method only in `Web`.
6. Preserve project scoping and authorization through `LogHub`.
7. Decide whether payload values should be structured DTOs rather than
   display strings.
8. Add unit tests for producers and the transport adapter.

Avoid adding source-specific methods such as `StreamDockerStatsAsync` or
`StreamAzureRevisionAsync`. The current contract describes user-visible
events, allowing different infrastructure providers to share the same UI.

## File Map

| File | Purpose |
|---|---|
| `ILogStreamer.cs` | Shared contract for build logs, container logs, and metrics. |
| `../../Web/Services/RealTimeLogStreamer.cs` | SignalR-backed implementation. |
| `../../Web/Hubs/ILogClient.cs` | Typed client methods receiving streamed events. |
| `../../Web/Hubs/LogHub.cs` | Project-group authorization and membership management. |

## Related Documentation

- [`Services`](../README.md)
- [`Services/Docker`](../Docker/README.md)
- [`Services/GitHub`](../GitHub/README.md)
- `Services/Azure` when its module README is added
- `Services/Orchestration` when its module README is added
- [`Web`](../../Web/README.md)
- [Solution navigation map](../../.agents/navigation.md)
