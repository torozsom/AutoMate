# Docker and Compose adapters

Local Docker SDK/CLI operations, image preparation and verified diagnostic sources.

## Contents

| Module                               | Responsibility                                                      |
|--------------------------------------|---------------------------------------------------------------------|
| [Build](Build/README.md)             | Temporary image contexts and synchronous build progress.            |
| [Cli](Cli/README.md)                 | Cancellable Docker/Compose processes and safe process setup.        |
| [Compose](Compose/README.md)         | Compose names, project-list JSON and host-port parsing.             |
| [Diagnostics](Diagnostics/README.md) | Verified sources, bounded decoding/replay and live metric sampling. |
| [Runtime](Runtime/README.md)         | Docker service, host options and disabled SaaS implementation.      |

## Usage and integration

Runtime implements IDockerService/IDockerDiagnosticSource; Cli executes cancellable processes; Build prepares
contexts/progress; Compose parses names/ports; Diagnostics verifies owned targets and normalizes daemon/log/metric
observations. Local orchestration supplies target inventories and supervises lifetimes.

## Contracts and lifecycle

Runtime collection is automatic for the latest active local deployment, independent of viewers/legacy flags. CLI output
and logs enter redaction before storage/presentation. Live numeric observations arrive independently of durable sampling
(default sixty seconds). Ownership labels and verified container IDs prevent cross-project capture.

Recovery uses bounded overlap/history and finite daemon history: it is best effort, not lossless. An initially absent
container can be a startup wait state; permission/connectivity failures and disappearance after inspection remain
observable failures. SaaS registers a disabled Docker service and capability gates.

## Related documentation

- [Parent module](../README.md)
- [Docker ports](../../Application/Abstractions/Docker/README.md)
- [Local workflow and supervision](../ApplicationServices/Orchestration/Local/README.md)
- [Collector tests](../../Test/Infra/Diagnostics/Docker/DockerCollectorTests.cs)
- [Ownership labels](../../Test/Infra/Templating/LocalOwnershipTemplateTests.cs)
