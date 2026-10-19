# Application data contracts

Owner-scoped account/project operations and bounded project, deployment and workspace reads.

## Contents

| Module                   | Responsibility                                                             |
|--------------------------|----------------------------------------------------------------------------|
| [Apps](Apps/README.md)   | Application lifecycle and owner-scoped project/deployment/workspace reads. |
| [Users](Users/README.md) | Account resolution and connected-Azure metadata.                           |

## Usage and integration

Apps separates lifecycle commands from focused read queries; Users resolves authenticated account identifiers and
connected-provider metadata. Infrastructure/ApplicationServices implements the contracts with persistence. UI/routes
should request the narrow read model they need rather than load a full entity graph by default.

## Contracts and lifecycle

Owner/project/deployment IDs must be checked together by implementations. Detached Domain-shaped graphs remain
compatibility results, not tracked aggregates. Legacy full-graph APIs and additive default query overloads remain
available; focused implementations provide richer paging/range behavior.

## Related documentation

- [Parent module](../README.md)
- [Persisted models](../../Domain/Entities/README.md)
- [Service implementations](../../Infrastructure/ApplicationServices/README.md)
- [Registrations](../../Web/Configs/Hosting/ServiceConfiguration.cs)
