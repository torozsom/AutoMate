# Docker

Docker and Docker Compose infrastructure adapter implementation.

Docker CLI output, container output, and metrics are normalized into Application deployment diagnostics before the
redacted event pipeline forwards safe terminal data.

## Source inventory

- `DockerBuildContextArchive.cs`
- `DockerBuildProgress.cs`
- `DockerCli.cs`
- `DockerComposeProjectParser.cs`
- `DockerContainerParameters.cs`
- `DockerMetricsLine.cs`
- `DockerNameNormalizer.cs`
- `DockerOptions.cs`
- `DockerPortParser.cs`
- `DockerProcessStartInfoFactory.cs`
- `DockerRegexes.cs`
- `DockerService.cs`

## Boundary

Infrastructure may reference Application and Domain, but never Web. It implements ports and owns provider-specific behavior.

## Related documentation

- [Solution navigation map](../../.agents/navigation.md)
