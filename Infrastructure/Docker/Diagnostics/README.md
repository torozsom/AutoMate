# Local diagnostic sources

Ownership-verified daemon/container observation, bounded decoding/replay and independent live metric delivery.

## Contents

| File                                                           | Types or asset                                | Responsibility                                                                                                |
|----------------------------------------------------------------|-----------------------------------------------|---------------------------------------------------------------------------------------------------------------|
| [DockerDiagnosticCollector.cs](DockerDiagnosticCollector.cs)   | `DockerDiagnosticCollector`, `DaemonProgress` | Owns cancellable Docker subscriptions, bounded parsing and durable overlap confirmation.                      |
| [DockerDiagnosticNormalizer.cs](DockerDiagnosticNormalizer.cs) | `DockerDiagnosticNormalizer`                  | Validates explicit ownership and normalizes daemon/Compose observations without forwarding provider payloads. |
| [DockerLogDecoder.cs](DockerLogDecoder.cs)                     | `DockerLogLine`, `DockerLogDecoder`           | A complete provider line or an explicit omission, before central redaction.                                   |
| [DockerLogReplayWindow.cs](DockerLogReplayWindow.cs)           | `DockerLogReplayWindow`                       | Deduplicates bounded timestamp overlap by occurrence, preserving identical legitimate repeated messages.      |
| [DockerMetricDelivery.cs](DockerMetricDelivery.cs)             | `DockerMetricDelivery`                        | Delivers each Docker observation live while sampling durable history at a separate cadence.                   |
| [DockerMetricsLine.cs](DockerMetricsLine.cs)                   | `DockerMetricsLine`                           | Parsed CPU and memory values from one Docker stats output line.                                               |

## Usage and integration

DockerDiagnosticCollector verifies immutable container IDs/labels and owns cancellable sources. Normalizer maps safe
daemon/Compose events. DockerLogDecoder incrementally handles independent UTF-8 streams/timestamps. ReplayWindow tracks
confirmed overlap occurrences. DockerMetricsLine parses stats; DockerMetricDelivery forwards live observations and
samples durable history.

## Contracts and lifecycle

Daemon feed requires explicit AutoMate/project/service/scope labels; web targets also require deployment identity.
Database ownership stays project/service scoped. Legacy containers require matching Compose association for output and
never enter the labeled daemon feed. Initially missing containers are wait states; permission/connection failure and
later disappearance remain failures.

Partial lines cap at 8,192 characters; oversize is omitted whole with notice. TTY output is combined/control, not
invented stderr. Replay tracks at most 4,096 timestamp/stream/redacted-message identities and occurrence counts;
identical legitimate repeated lines survive. Suppress only durable confirmations, request one-second overlap and recover
at most 500 recent records. Initial tail is 100 lines; bounded daemon/overlap recovery is best effort.

Every live metric observation is redacted and sent independently of sampled history (default sixty seconds). Dispatcher
must not overwrite newer live metrics with sampled observations. Cancel/await all subscriptions and expose explicit gaps
rather than detached task failures.

## Related documentation

- [Parent module](../README.md)
- [Supervisor](../../ApplicationServices/Orchestration/Local/README.md)
- [Source/target ports](../../../Application/Abstractions/Docker/README.md)
- [Collector tests](../../../Test/Infra/Diagnostics/Docker/DockerCollectorTests.cs)
- [Decoder/replay tests](../../../Test/Infra/Diagnostics/Docker/DockerOutputTests.cs)
- [Metrics tests](../../../Test/Infra/Diagnostics/Docker/DockerMetricDeliveryTests.cs)
