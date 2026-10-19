# Owner-scoped data services

Persistence-backed project/workspace and account implementations of Application data contracts.

## Contents

| Module                   | Responsibility                                                |
|--------------------------|---------------------------------------------------------------|
| [Apps](Apps/README.md)   | Lifecycle commands and bounded owner-scoped read projections. |
| [Users](Users/README.md) | Account identifier and connected-Azure lookups.               |

## Usage and integration

Apps owns imports/deletion/consent edits and focused project/deployment/workspace reads. Users resolves authenticated
identifiers and connected Azure metadata. Presentation consumers use Application contracts; hosts register
implementations.

## Contracts and lifecycle

Filter ownership at the database boundary. Focused queries return detached graphs/projections and bounded pages rather
than tracked complete aggregates. Preserve legacy APIs while choosing narrower ports for new consumers.
Credentials/environment values must not leak into projections or telemetry.

## Related documentation

- [Parent module](../README.md)
- [Contracts](../../../Application/Data/README.md)
- [Persistence](../../Data/README.md)
- [Registration](../../../Web/Configs/Hosting/ServiceConfiguration.cs)
