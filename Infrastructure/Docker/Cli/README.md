# Docker process execution

Cancellable Docker/Compose CLI orchestration, stats streaming and project/host-port lookup.

## Contents

| File                                                                 | Types or asset                  | Responsibility                                                                                 |
|----------------------------------------------------------------------|---------------------------------|------------------------------------------------------------------------------------------------|
| [DockerCli.cs](DockerCli.cs)                                         | `DockerCli`                     | Executes Docker CLI commands needed for Compose, metrics, port discovery, and project listing. |
| [DockerProcessStartInfoFactory.cs](DockerProcessStartInfoFactory.cs) | `DockerProcessStartInfoFactory` | Creates Docker CLI process start information with consistent redirection and shell settings.   |
| [DockerRegexes.cs](DockerRegexes.cs)                                 | `DockerRegexes`                 | Centralizes generated regular expressions used by Docker parsing helpers.                      |

## Usage and integration

DockerCli starts commands using DockerProcessStartInfoFactory, consumes redirected output and maps observations into
deployment diagnostics. DockerRegexes supplies bounded generated parsing helpers. Runtime DockerService delegates
Compose, stats and discovery to this module.

## Contracts and lifecycle

Use argument-list/process settings rather than shell-built customer command strings. Read stdout/stderr concurrently,
await readers and kill the process tree on timeout/cancellation. Preserve exit/timeout/cancellation distinctions and
deployment correlation for stop diagnostics.

CLI output is normalized/redacted terminal data, not free-form platform telemetry. Log finite command outcome/exit
status and GUIDs without paths, arguments, names or secrets. Numeric stats have independent live-delivery and
durable-sampling cadence.

## Related documentation

- [Parent module](../README.md)
- [Service](../Runtime/README.md)
- [Parsing/names](../Compose/README.md)
- [Metric delivery](../Diagnostics/README.md)
- [Output tests](../../../Test/Infra/Diagnostics/Docker/DockerOutputTests.cs)
- [Sampling tests](../../../Test/Infra/Diagnostics/Docker/DockerMetricDeliveryTests.cs)
