# Project presentation helpers

Status/workflow badges, persisted GitHub URL parsing and scoped section navigation.

## Contents

| File                                                             | Types or asset              | Responsibility                                                                               |
|------------------------------------------------------------------|-----------------------------|----------------------------------------------------------------------------------------------|
| [DeploymentBadge.razor](DeploymentBadge.razor)                   | `DeploymentBadge`           | Presents deployment state using a consistent text, icon and badge style.                     |
| [GitHubRepositoryReference.cs](GitHubRepositoryReference.cs)     | `GitHubRepositoryReference` | Identifies a GitHub repository by owner and repository name.                                 |
| [GitHubRepositoryUrlParser.cs](GitHubRepositoryUrlParser.cs)     | `GitHubRepositoryUrlParser` | Parses persisted GitHub repository URLs into repository references for deployment workflows. |
| [ProjectSectionNavigation.razor](ProjectSectionNavigation.razor) | `ProjectSectionNavigation`  | Presents project section links with scoped scroll tracking and fragment restoration.         |
| [WorkflowBadge.razor](WorkflowBadge.razor)                       | `WorkflowBadge`             | Presents remote workflow progress with an optional GitHub Actions link.                      |

## Usage and integration

DeploymentBadge renders recorded runtime state. WorkflowBadge adds remote progress/link context.
GitHubRepositoryUrlParser produces GitHubRepositoryReference for existing repository-source actions.
ProjectSectionNavigation mounts project-sections.js for active links, keyboard/focus scroll and fragment restoration.

## Contracts and lifecycle

Badges describe recorded status, not runtime health or preparation outcome; retain readable text/icons alongside color.
Repository parsing supplies owner/name syntax, not proof of repository permission. Authorized cloud admission performs
access checks.

Section links operate within the mounted console, account for sticky headers, respect reduced motion and restore
fragments without unnecessary history. Dispose listeners/scheduled frames when project/deployment changes. Keep section
IDs aligned with the page rather than register global selectors across unrelated consoles.

## Related documentation

- [Parent module](../README.md)
- [Status meaning](../../../../Domain/Enums/Deployments/README.md)
- [Page sections](../../Pages/Projects/README.md)
- [Navigation module](../../../wwwroot/js/project-sections.js)
- [Scroll/focus tests](../../../../Test/Web/Browser/project-sections.test.cjs)
- [Badge/render tests](../../../../Test/Web/Presentation/ConsoleRenderingTests.cs)
