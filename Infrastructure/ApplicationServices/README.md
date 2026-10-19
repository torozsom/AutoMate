# EF-backed application services

Concrete account/project services and deployment workflows that currently require direct metadata persistence.

## Contents

| Module                                   | Responsibility                                                        |
|------------------------------------------|-----------------------------------------------------------------------|
| [Auth](Auth/README.md)                   | Registration, verification, login and connected-provider persistence. |
| [Data](Data/README.md)                   | Owner-scoped project/workspace and account persistence.               |
| [Orchestration](Orchestration/README.md) | Local/cloud launch, monitoring and history lifecycle.                 |

## Usage and integration

Auth implements Application authentication; Data implements owner-scoped lifecycle/read services; Orchestration
coordinates local/cloud preparation, status and runtime supervision. These are current EF-backed implementations, not a
new Application-layer dependency on EF.

## Contracts and lifecycle

Do not return tracked aggregates merely because a contract uses Domain-shaped data. Focused query services return
detached, owner-scoped projections; legacy graph reads remain for compatibility. Audit logs use internal GUIDs and
finite state, not names/emails/tokens. Explicit execution clocks retain existing constructors and system defaults.

## Related documentation

- [Parent module](../README.md)
- [Auth contract](../../Application/Auth/README.md)
- [Data contracts](../../Application/Data/README.md)
- [Workflow ports](../../Application/Orchestration/README.md)
- [Persistence](../Data/README.md)
