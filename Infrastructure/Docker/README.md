# Docker

DockerContainerNotFoundException during initial container creation is a wait state: logs emit one informational
waiting notice and retry; metric acquisition returns without a failure warning or repeated terminal notice.
Permission/connectivity failures and a previously inspected container disappearing remain observable failures.
The platform failure-type allowlist preserves DockerContainerNotFoundException without exposing its response body.
No API version or Docker permissions were changed. Default collector tests exercise startup races and permission denial.

Docker and Docker Compose infrastructure adapter implementation.

Docker CLI output, container output, and metrics are normalized into Application deployment diagnostics before the
redacted event pipeline forwards safe terminal data.
Local Compose build and container log events carry the deployment ID so history stays separate across redeployments.
Compose execution and container log subscription activities report source-only failure counters. Live metric writes
honor transport cancellation and report delivery latency/failures without changing the independent durable sampling
cadence.
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

## Local diagnostic coverage

`DockerDiagnosticCollector` uses the supported Docker SDK event callback API and verified immutable container IDs.
Daemon events require explicit `io.automate.owner=AutoMate`, project, service and scope labels. Web containers also
require
the active deployment ID; database containers use stable project/service ownership and are correlated with the
registered
deployment. Only create/start/restart/health/die/stop/kill/destroy actions and a small attribute allowlist are
forwarded.
Daemon observations go to Build. Pre-label containers remain viewable only after matching their Compose project/service;
they never enter the daemon feed. New labels apply on the next normal deployment; recovery does not recreate containers.

`DockerDiagnosticNormalizer` recognizes Compose/BuildKit progress, image identities and container transitions. Command
exit outcomes are structured; unfamiliar/blank text and CR/LF remain readable. Stderr is a stream, not an automatic
error
severity. Legacy SDK image-build progress is redacted before host logging and handled synchronously. Provider exception
bodies are excluded from collector logs. CLI readers are awaited and process trees are killed on timeout/cancellation.

`DockerLogDecoder` incrementally decodes independent UTF-8 stdout/stderr streams, Docker timestamps and EOF partial
lines.
Raw partial lines are bounded at 8,192 characters; an oversized line is omitted whole with a safe notice. A TTY's
combined
output is marked as combined/control, with a visible notice instead of inventing a stderr distinction.

`DockerLogReplayWindow` tracks up to 4,096 timestamp/stream/redacted-message identities and occurrence counts.
Legitimate
identical repeated lines survive; only durably confirmed occurrences are suppressed. Reconnect requests a one-second
overlap from the last confirmed timestamp. Restart recovery reads up to 500 recent safe records from the existing
disk/Loki history, with no PostgreSQL payload fallback. Initial output retains the existing 100-line tail. Reconnect
backoff is bounded at 30 seconds, subscriptions have cancellable lifetimes, and EOF/permission/connectivity/replay gaps
produce safe notices. Docker's finite daemon history and bounded overlap mean recovery is best effort, not lossless.
The daemon admission queue holds at most 256 already-redacted events; overflow produces an explicit gap notice.

Tests: `DockerOutputTests`, `DockerCollectorTests`, `LocalOwnershipTemplateTests` and `LocalDiagnosticSupervisionTests`.
Opt-in idle daemon/log tests verify cancellation without lifecycle activity or stopping the container. The Compose
smoke test also stops a running container with event/log/metric subscriptions active and awaits all source cleanup.
The real Docker/Compose smoke tests are opt-in, use a pre-existing image, expose no ports/mounts and remove only their
containers and Compose network. Example: set `AUTOMATE_DOCKER_SMOKE=1` and
`AUTOMATE_DOCKER_SMOKE_IMAGE=busybox:1.37`, then run
`dotnet test Infrastructure.Tests --filter FullyQualifiedName~DockerCollectorTests.Real_`.

Build/Compose, daemon/output and metric subscription spans have fixed names, safe outcomes and GUID correlation.
Paths, arguments, names and output are excluded from custom tags. Normal metric shutdown is Debug; unexpected
cancellation remains Warning. Exit/timeout/cancellation behavior is unchanged. Real Docker tests remain opt-in.

Image/container/Compose operational logs omit names and filesystem paths; GUIDs and numeric port values retain
correlation. Build-context cleanup logs expose failure type only. Redacted build progress and diagnostic terminal
delivery remain unchanged.

Ownership-verified Docker observations record the selected deployment’s application container identity and actual image
in its immutable configuration snapshot. Database credentials, environment values and inspect payloads are excluded.
