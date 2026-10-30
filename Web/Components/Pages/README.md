# Routed page presenters

Workspace/project/deployment views, repository imports, authentication feedback and safe errors.

DeploymentHistory's markup, code-behind and scoped CSS live in this directory to preserve its
`Web.Components.Pages.DeploymentHistory` public type. The Projects guide documents its behavior.

## Contents

| Module                                     | Responsibility                                             |
|--------------------------------------------|------------------------------------------------------------|
| [Authentication](Authentication/README.md) | Local login, registration and email verification.          |
| [Errors](Errors/README.md)                 | Safe error and missing-route presentation.                 |
| [Identity](Identity/README.md)             | Authenticated principal-to-owner resolution.               |
| [Projects](Projects/README.md)             | Current project console and historical deployment details. |
| [Repositories](Repositories/README.md)     | Local discovery and GitHub repository import.              |
| [Workspace](Workspace/README.md)           | Public/signed-in Home and project inventory.               |

## Usage and integration

Use Workspace for Home/Projects, Projects for current or historical deployments, Repositories for source import,
Authentication for local forms and Identity for owner resolution. Pages combine Application data with Shared presenters;
Configs remains the concrete composition boundary.

## Contracts and lifecycle

Use independent scopes for overlapping EF-backed operations. Renderer-dispatched notifications coalesce refreshes;
disposal/navigation cancels work and rejects late results. Current action metadata and bounded history use focused query
ports, with existing legacy fallbacks retained. Routes expose current UI state; host/operation authorization remains
required.

## Related documentation

- [Parent module](../README.md)
- [Read contracts](../../../Application/Data/Apps/README.md)
- [Scope/replay boundaries](../../Services/README.md)
- [Notification tests](../../../Test/Web/Projects/ProjectDetailsNotificationTests.cs)
- [Dispatcher tests](../../../Test/Web/Projects/DashboardStatusTests.cs)
