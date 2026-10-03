# Docker

Docker and Docker Compose infrastructure adapter implementation.

Docker CLI output, container output, and metrics are normalized into Application deployment diagnostics before the
redacted event pipeline forwards safe terminal data.
Local Compose build and container log events carry the deployment ID so history stays separate across redeployments.
Local deployment preparation checks for another project's normalized Compose name and verifies a new deployment's
host port before starting Docker. A conflict is reported as a redacted build diagnostic on the failed deployment.

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

Infrastructure may reference Application and Domain, but never Web. It implements ports and owns provider-specific
behavior.

## Related documentation

- [Solution navigation map](../../.agents/navigation.md)
