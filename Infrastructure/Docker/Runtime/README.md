# Docker runtime service

IDockerService/IDockerDiagnosticSource implementation, options/container parameters and disabled SaaS placeholder.

## Contents

| File                                                         | Types or asset              | Responsibility                                                                                                                                                         |
|--------------------------------------------------------------|-----------------------------|------------------------------------------------------------------------------------------------------------------------------------------------------------------------|
| [DisabledDockerService.cs](DisabledDockerService.cs)         | `DisabledDockerService`     | Safe placeholder used by hosted SaaS instances. It prevents any Docker daemon interaction while keeping pages that display historical local deployments constructible. |
| [DockerContainerParameters.cs](DockerContainerParameters.cs) | `DockerContainerParameters` | Creates Docker.DotNet container creation parameter objects from AutoMate deployment settings.                                                                          |
| [DockerOptions.cs](DockerOptions.cs)                         | `DockerOptions`             | Configuration options for the Docker Service.                                                                                                                          |
| [DockerService.cs](DockerService.cs)                         | `DockerService`             | Coordinates Docker daemon operations used by local deployments and runtime log streaming.                                                                              |

## Usage and integration

DockerService coordinates SDK daemon/build/container operations and CLI helpers, and owns/disposes its Docker client.
DockerContainerParameters maps prepared settings. DockerOptions selects platform daemon endpoints and timeouts.
DisabledDockerService keeps historical UI construction possible without contacting Docker in SaaS.

## Contracts and lifecycle

Defaults include container port 8080, Compose timeout eight minutes and standard Windows pipe/Unix socket endpoints.
Environment JSON is secret-bearing. Preserve explicit ownership/container verification before collecting output and
propagate actual cancellation into SDK/process work.

Capability gates and disabled implementation prevent local execution on cloud-only hosts; disabled operations are not
successful daemon health. Build/start/Compose return flags/identities rather than readiness guarantees. Source lifetime
ownership belongs to host supervision, not a page-scoped service or detached Task.Run.

## Related documentation

- [Parent module](../README.md)
- [Ports](../../../Application/Abstractions/Docker/README.md)
- [Build helpers](../Build/README.md)
- [Processes](../Cli/README.md)
- [Sources](../Diagnostics/README.md)
- [Host composition](../../../Test/Web/Hosting/HostingProfileRegistrationTests.cs)
- [Source tests](../../../Test/Infra/Diagnostics/Docker/DockerCollectorTests.cs)

DockerService passes DeploymentDiagnosticOptions live cadence to each CLI collector; callers omitting the optional
options parameter retain the 1,000 ms default. Existing runtime/history/ownership contracts remain unchanged.
