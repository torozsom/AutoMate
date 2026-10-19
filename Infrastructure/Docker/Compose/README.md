# Compose parsing and names

Deterministic Compose project names, running-project JSON parsing and mapped-host-port extraction.

## Contents

| File                                                           | Types or asset               | Responsibility                                                |
|----------------------------------------------------------------|------------------------------|---------------------------------------------------------------|
| [DockerComposeProjectParser.cs](DockerComposeProjectParser.cs) | `DockerComposeProjectParser` | Parses JSON output from docker compose ls --format json.      |
| [DockerNameNormalizer.cs](DockerNameNormalizer.cs)             | `DockerNameNormalizer`       | Normalizes external names into Docker-compatible identifiers. |
| [DockerPortParser.cs](DockerPortParser.cs)                     | `DockerPortParser`           | Parses mapped host ports from docker port command output.     |

## Usage and integration

DockerNameNormalizer sanitizes names into identifiers. DockerComposeProjectParser reads docker compose ls JSON;
DockerPortParser extracts host mappings with shared regexes. DockerCli/Runtime use these results; orchestration checks
conflicts before side effects.

## Contracts and lifecycle

Normalization can collapse different customer names to the same Compose identity, so preserve conflict checks and
existing naming compatibility. Parsed ports/projects describe daemon output, not proof of ownership or application
health. Keep malformed/empty-output behavior aligned with callers instead of inventing values.

## Related documentation

- [Parent module](../README.md)
- [Callers](../Cli/README.md)
- [Conflict checks](../../ApplicationServices/Orchestration/Local/README.md)
- [Conflict tests](../../../Test/Infra/Orchestration/LocalHostPortAvailabilityTests.cs)
- [Parsing regressions](../../../Test/Infra/Diagnostics/Docker/DockerOutputTests.cs)
