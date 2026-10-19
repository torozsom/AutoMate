# Image build preparation

Temporary tar contexts and synchronous Docker SDK build-progress handling.

## Contents

| File                                                         | Types or asset              | Responsibility                                                                        |
|--------------------------------------------------------------|-----------------------------|---------------------------------------------------------------------------------------|
| [DockerBuildContextArchive.cs](DockerBuildContextArchive.cs) | `DockerBuildContextArchive` | Creates temporary tar archives used as Docker image build contexts.                   |
| [DockerBuildProgress.cs](DockerBuildProgress.cs)             | `DockerBuildProgress`       | Tracks Docker image build progress and records whether Docker reported a build error. |

## Usage and integration

DockerBuildContextArchive applies configured/context ignore rules while building the temporary archive used by
DockerService. DockerBuildProgress implements IProgress<JSONMessage>, records HasError and sends redacted progress
through reviewed logging. Runtime owns the build operation and archive cleanup.

## Contracts and lifecycle

Build inputs can contain private files/secrets: preserve ignore behavior and temporary-file ownership/cleanup. Never log
archive contents or raw provider exceptions. Progress callbacks are synchronous so failure accounting cannot race
detached asynchronous callbacks. A completed SDK call is not successful if Docker reported build errors.

## Related documentation

- [Parent module](../README.md)
- [Build consumer](../Runtime/README.md)
- [Compose path](../Cli/README.md)
- [Ignore/build templates](../../Templating/Templates/README.md)
- [Output tests](../../../Test/Infra/Diagnostics/Docker/DockerOutputTests.cs)
