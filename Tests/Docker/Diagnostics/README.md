# Docker diagnostic parsing tests

| Suite                                                       | Methods/cases | Verifies                                                                                                  |
|-------------------------------------------------------------|---------------|-----------------------------------------------------------------------------------------------------------|
| [DockerMetricsLineTests](DockerMetricsLineTests.cs)         | 2/2           | ANSI removal, trimmed CPU/memory values and missing delimiters.                                           |
| [DockerLogLineBoundaryTests](DockerLogLineBoundaryTests.cs) | 3/3           | CRLF split across frames and one-time flushing of partial lines.                                          |
| [DockerLogUtf8Tests](DockerLogUtf8Tests.cs)                 | 2/3           | Every byte split with timestamps enabled/disabled, plus empty frames.                                     |
| [DockerLogTimestampTests](DockerLogTimestampTests.cs)       | 2/2           | UTC conversion of valid timestamps and preservation of invalid prefixes.                                  |
| [DockerLogOverflowTests](DockerLogOverflowTests.cs)         | 2/3           | Exact line limit and one parameterized overflow/recovery check for ordinary and credential-bearing input. |

Only in-memory strings and byte arrays are decoded. No subscriptions or cross-module event types are involved.
Fallback clock values are not asserted; provider timestamps use fixed inputs.

See the [production guide](../../../Infrastructure/Docker/Diagnostics/README.md) and [parent module](../README.md).

Migrated decoder scenarios additionally check every Unicode byte split, independent stream buffers and omission
before a secret-bearing oversized line can be emitted.

Overlapping migrated UTF-8 and overflow methods are consolidated into theories. Theories retain both timestamp modes,
all byte boundaries, both newline styles, no premature secret output, exact omission markers and successful recovery.
