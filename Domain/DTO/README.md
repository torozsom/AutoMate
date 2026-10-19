# Data transfer contracts

Shared inputs and projections exchanged by application ports, scanners and provider adapters.

## Contents

| Module                             | Responsibility                                                     |
|------------------------------------|--------------------------------------------------------------------|
| [Azure](Azure/README.md)           | Credentials, federated-identity setup and monitoring requests.     |
| [Deployment](Deployment/README.md) | Local/cloud configuration, databases and repository launch inputs. |
| [GitHub](GitHub/README.md)         | Repository import, workflow progress and log projections.          |
| [Projects](Projects/README.md)     | Filesystem discovery, .NET metadata and detection rules.           |
| [Templating](Templating/README.md) | Manifest rules and generated artifacts.                            |

## Usage and integration

Choose a child module by workflow, then follow its concrete consumer
links. [Application ports](../../Application/Abstractions/README.md) define operations using these contracts;
Infrastructure performs scanning, rendering and provider interactions. Persisted ownership and execution state belong
to [Entities](../Entities/README.md).

## Contracts and lifecycle

Records here are not uniformly immutable: several expose mutable collections, and DeploymentConfigDto is a mutable
class. Empty strings and collections are initialization defaults, not evidence of valid input. Some DTOs contain
credentials or generated secret-bearing content; consult their leaf guides before logging or persisting them. DTO
construction does not validate paths, authorize owners or protect stored values.

## Related documentation

- [Domain](../README.md)
- [Deployment defaults](../Defaults/README.md)
- [Infrastructure adapters](../../Infrastructure/README.md)
