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

Runtime collectors run while an authorized project page is viewing the deployment, or follow explicit owner opt-in
without a browser. Viewed output is saved for replay. `LocalRuntimeRecoveryService` restores missing collectors
after host restart using the current project dependency configuration. Numeric CPU samples preserve cores (Docker
percent /
100); memory uses bytes. `DockerMetricDelivery` forwards every Docker stats observation to authorized live viewers
(normally every 1–2 seconds), independently of storage. Durable metric samples default to 60 seconds. The dispatcher
does not resend sampled Docker metrics, avoiding stale values overwriting newer live observations. Collectors are
cancelled on host shutdown.
