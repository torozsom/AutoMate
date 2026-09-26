# Docker

`Services/Docker` is AutoMate's local Docker integration. It provides the
application-facing `IDockerService` contract for checking the Docker daemon,
building images, starting containers, running Docker Compose deployments,
discovering mapped ports, and streaming container output and metrics.

The module is an infrastructure adapter. It does not decide when a project
should be deployed, create deployment records, generate Dockerfiles, render
Compose files, or own the UI. Those responsibilities belong to
`Services.Orchestration`, `Services.Templating`, `Core`, and `Web`.

## Responsibilities

- Connect to the platform-specific Docker daemon.
- Build image contexts while honoring `.dockerignore`.
- Build Docker images through Docker.DotNet.
- Create and start individual containers through Docker.DotNet.
- Run `docker compose up -d --build` and `docker compose down`.
- Stream Compose process output as build logs.
- Stream container logs through Docker.DotNet.
- Stream Docker stats CPU and memory values through `ILogStreamer`.
- List active Compose project names for deployment reconciliation.
- Resolve a container's mapped host port.
- Normalize project names and parse Docker CLI output.
- Apply configured command timeouts and cancellation.

## Public Contract

`IDockerService` is the boundary used by the rest of the solution:

| Method | Purpose | Result on ordinary operational failure |
|---|---|---|
| `PingAsync` | Check Docker daemon responsiveness. | `false` |
| `BuildImageAsync` | Build an image from a source directory. | `false` |
| `StartContainerAsync` | Create/start one container with environment and port bindings. | `null` |
| `RunDockerComposeUpAsync` | Start the generated local Compose project. | `false` |
| `RunDockerComposeDownAsync` | Stop/remove the generated local Compose project. | `false` |
| `GetRunningProjectNamesAsync` | List active Compose project names. | Empty list |
| `StreamContainerLogsAsync` | Follow a container's stdout/stderr. | Logs the failure and completes |
| `StreamContainerMetricsAsync` | Follow Docker stats output. | Logs the failure and completes |
| `GetContainerHostPortAsync` | Resolve the host port mapped to a container. | `0` |

The result conventions are intentional: the caller can decide whether a
failed Docker operation should fail a deployment, display a degraded status,
or retry a UI lookup. Do not silently turn a failed deployment command into a
successful result.

## Local Deployment Flow

The normal local deployment path is coordinated outside this module:

```text
LocalDeploymentOrchestrator
    -> Scanner finds solution/project metadata
    -> Templating writes .automate/Dockerfile and docker-compose.yml
    -> IDockerService.RunDockerComposeUpAsync
        -> docker compose -p <safe-name> up -d --build
    -> DeploymentStatus.Running
    -> LocalDeploymentLogStreamManager
        -> container logs + metrics
```

Stopping uses the same Compose project name:

```text
IDockerService.RunDockerComposeDownAsync
    -> docker compose -p <safe-name> down
    -> stop active stream workers
    -> DeploymentStatus.Stopped
```

The generated Compose file normally exposes the web container's internal
port `8080` on the configured host port. Database containers are placed on
the Compose network and receive generated service/container names.

## Docker.DotNet and Docker CLI Split

The implementation intentionally uses both Docker.DotNet and the Docker CLI:

| Operation | Implementation |
|---|---|
| Docker daemon ping | Docker.DotNet |
| Image build | Docker.DotNet |
| Individual container creation/start | Docker.DotNet |
| Compose up/down | Docker CLI |
| Running Compose project listing | Docker CLI |
| Container host-port lookup | Docker CLI |
| Container stats | Docker CLI |
| Container log following | Docker.DotNet |

Docker.DotNet is used where the Engine API provides a direct structured
operation. The CLI is retained for Compose and output formats that are
already stable and convenient to parse. Do not replace one mechanism without
checking process lifecycle, output parsing, cancellation, and testability.

## Docker Client and Platform Configuration

`DockerService` selects the daemon endpoint from `DockerOptions`:

| Platform | Option | Default |
|---|---|---|
| Windows | `WindowsDockerUri` | `npipe://./pipe/docker_engine` |
| Unix-like | `UnixDockerUri` | `unix:///var/run/docker.sock` |

Other options:

| Option | Default | Purpose |
|---|---:|---|
| `DefaultContainerPort` | `8080` | Internal container port used when the caller supplies the interface default. |
| `ComposeTimeoutMinutes` | `8` | Maximum duration for a Compose command. |
| `DefaultDockerIgnore` | `bin/`, `obj/`, `.git/`, `.vs/`, `node_modules/`, `TestResults/`, `.DS_Store` | Fallback build-context exclusions. |

The options class is bound from the `Docker` configuration section by
`Web.Configs.ServiceConfiguration`. Keep the daemon URI and timeout
configuration outside source code for deployment-specific environments.

`DockerService` owns and disposes its Docker.DotNet client. It is registered
as a scoped service. Background stream managers create a fresh service scope
so the scoped client remains valid for the lifetime of the stream workers.

## Build Contexts and Images

`DockerBuildContextArchive` creates a temporary tar archive from a source
directory:

1. Verify the source directory exists.
2. Load the source `.dockerignore` when present.
3. Otherwise apply `DockerOptions.DefaultDockerIgnore`.
4. Enumerate files recursively.
5. Convert relative paths to `/` separators.
6. Write non-ignored files to the tar archive.
7. Delete the temporary archive in a `finally` block.

`BuildImageAsync` sends the archive to Docker.DotNet and tracks
`JSONMessage` progress through `DockerBuildProgress`. A build is considered
failed if Docker reports an `ErrorMessage`, even if the transport operation
itself completes.

Temporary archive cleanup is best-effort. Cleanup failures are logged as
warnings without hiding the original build result.

## Individual Container Operations

`StartContainerAsync` creates a container with:

- the requested image tag;
- the requested container name;
- an exposed TCP container port;
- a host-port binding;
- environment variables parsed from a JSON object.

`DockerContainerParameters` converts persisted environment JSON into Docker's
`KEY=VALUE` list. Invalid JSON is not converted into a successful container
configuration; callers should validate environment configuration before
invoking the service.

When `containerPort == 8080`, `DockerService` uses
`DockerOptions.DefaultContainerPort`; otherwise it honors the explicit port.
The method returns the Docker container ID only after a successful start.

## Compose Process Execution

`DockerProcessStartInfoFactory` always creates non-shell, hidden processes with
redirected output. Compose commands are built as argument lists rather than
shell command strings:

```text
docker compose -p <normalized-project-name> up -d --build
docker compose -p <normalized-project-name> down
```

This avoids shell interpolation and keeps project names as a separate
argument. `DockerNameNormalizer.NormalizeProjectName`:

- trims and lowercases the name;
- replaces unsafe characters with `-`;
- removes repeated/empty hyphen segments;
- falls back to `automate-project` when no usable name remains.

Compose stdout and stderr are forwarded to `ILogStreamer.StreamBuildLogsAsync`
for the deployment project. The configured Compose timeout and caller
cancellation share a linked cancellation token. Timeout or cancellation
kills the process tree and returns `false`.

Process descriptions are built from the argument list for diagnostics. Do not
include secrets or environment values in new command arguments.

## Logs and Metrics

### Build and Compose logs

Compose process output is sent to the build-log channel:

```text
Docker CLI stdout/stderr
    -> ILogStreamer.StreamBuildLogsAsync(projectId, line)
    -> Web real-time log transport
```

Empty lines are ignored. The service appends `\r\n` to forwarded lines for
the terminal-style UI.

### Container logs

`DockerService.StreamContainerLogsAsync` follows stdout and stderr, requests
the last 100 lines as an initial tail, and reads the Docker multiplexed stream
until EOF or cancellation. A rented `ArrayPool<byte>` buffer is returned in a
`finally` block.

The caller supplies `containerSuffixOrTabId`, which is passed to
`ILogStreamer` as the UI stream identifier. The Docker module does not know
about SignalR groups or Blazor components.

### Container metrics

`DockerCli.StreamContainerMetricsAsync` executes:

```text
docker stats <container> --format "{{.CPUPerc}}|{{.MemUsage}}"
```

`DockerMetricsLine` removes ANSI escape sequences and parses the first two
pipe-separated fields. Parsed values are forwarded unchanged as display
strings for CPU and memory. Metrics are streamed until cancellation or
process termination.

`LocalDeploymentLogStreamManager` starts both log and metric streams for the
web container and each configured database container. The Azure module uses a
separate implementation for cloud metrics but the same `ILogStreamer`
contract.

## Port Discovery

`GetContainerHostPortAsync` runs `docker port <container>` and parses the first
host port matching `:<digits>`. It has a ten-second lookup timeout and returns
`0` for cancellation, process failure, or an unparseable response.

`Web.Components.Pages.ProjectDetails` uses this result after a local
deployment reaches `DeploymentStatus.Running` to construct the project URL.
Port lookup is a best-effort UI operation and should not be treated as proof
that the deployment itself succeeded.

## Reconciliation and Health Checks

Docker has consumers beyond deployment execution:

- `Web.Components.Layout.MainLayout` calls `PingAsync` after the first render
  so a slow Docker socket does not block initial page rendering.
- `DeploymentCleanupHostedService` calls
  `GetRunningProjectNamesAsync` at startup and synchronizes persisted
  `Running`/`Stopped` deployment records with active Compose projects.
- `LocalDeploymentOrchestrator` uses Compose up/down and starts/stops stream
  workers around deployment lifecycle transitions.

`docker compose ls --format json` output is parsed by
`DockerComposeProjectParser`. Empty, invalid, or non-array output should not
be interpreted as an active project list.

## Error Handling and Cancellation

The module uses explicit operational failure results at the public boundary:

- `PingAsync`: logs non-cancellation failures and returns `false`;
- build/start/Compose: logs failures and returns `false`/`null`;
- project listing: logs failures and returns an empty list;
- port lookup: logs failures and returns `0`;
- log/metric streams: log cancellation normally and unexpected failures as
  errors, then complete the stream task.

Cancellation must be passed to Docker.DotNet, file I/O, process reads, waits,
and delays. CLI cancellation kills the process tree to avoid orphaned
`docker`/Compose processes.

The process cleanup helper intentionally performs best-effort cleanup because
cancellation can race with natural process exit. Do not turn that race into a
new deployment failure.

## Security and Safety Rules

- Use `ProcessStartInfo.ArgumentList`; do not build shell command strings from
  user-controlled names.
- Normalize project/container-derived names before using them as Docker
  identifiers.
- Do not log passwords, connection strings, environment-variable values,
  tokens, or complete Docker configuration objects.
- Apply `.dockerignore` to avoid sending secrets, build output, IDE metadata,
  and unrelated files to the daemon.
- Treat source directories and generated Compose files as potentially
  sensitive.
- Keep Docker socket permissions and host security outside this module; access
  to the daemon is effectively privileged.
- Do not expose raw Docker daemon errors directly to users when they contain
  host paths or environment details.

## Dependency Injection

`Web.Configs.ServiceConfiguration.RegisterDomainServices` registers:

```csharp
services.AddScoped<IDockerService, DockerService>();
```

`DockerService` depends on:

- `ILogger<DockerService>`;
- `ILogStreamer`;
- `IOptions<DockerOptions>`.

It creates internal `DockerBuildContextArchive` and `DockerCli` helpers.
`DockerCli` also depends on `ILogStreamer` to forward build and metrics
output.

## File Map

| File | Purpose |
|---|---|
| `IDockerService.cs` | Public Docker integration contract. |
| `DockerService.cs` | Coordinates Docker.DotNet and CLI operations. |
| `DockerOptions.cs` | Platform endpoints, timeouts, and ignore defaults. |
| `DockerBuildContextArchive.cs` | Creates `.dockerignore`-aware tar contexts. |
| `DockerBuildProgress.cs` | Tracks Docker.DotNet image-build progress/errors. |
| `DockerCli.cs` | Runs Compose, stats, project-list, port, and log-stream CLI operations. |
| `DockerContainerParameters.cs` | Builds Docker.DotNet container parameters. |
| `DockerProcessStartInfoFactory.cs` | Creates safe Docker process arguments and descriptions. |
| `DockerNameNormalizer.cs` | Normalizes Compose project names. |
| `DockerComposeProjectParser.cs` | Parses `docker compose ls --format json`. |
| `DockerPortParser.cs` | Parses host ports from `docker port` output. |
| `DockerMetricsLine.cs` | Parses formatted Docker stats lines. |
| `DockerRegexes.cs` | Generated regular expressions for Docker output parsing. |

## Extending the Module

Before adding a Docker capability:

1. Decide whether it belongs in the public `IDockerService` contract or is
   only a helper for an existing operation.
2. Prefer Docker.DotNet for structured Engine API operations and the CLI for
   Compose or stable command output that the current module already parses.
3. Add a focused parser/helper for new output formats; do not embed complex
   regular expressions in orchestration code.
4. Preserve argument-list process creation, timeout, cancellation, and process
   tree cleanup.
5. Define an explicit failure result and logging level.
6. Route user-visible logs and metrics through `ILogStreamer`, not directly
   to SignalR or Blazor.
7. Update `DockerOptions` and configuration documentation for new runtime
   settings.
8. Update `Services.Orchestration` and the relevant template/module README
   when the new operation changes deployment behavior.

Keep `DockerService` focused on Docker mechanics. Resource naming policy,
deployment status transitions, project selection, and database orchestration
belong elsewhere.

## Related Documentation

- [`Services`](../README.md)
- `Services/Orchestration` when its module README is added
- `Services/Templating` when its module README is added
- `Services/LogStreaming` when its module README is added
- [`Core/Entities`](../../Core/Entities/README.md)
- [`Core/DTO`](../../Core/DTO/README.md)
- [`Web`](../../Web/README.md)
- [Solution navigation map](../../.agents/navigation.md)
