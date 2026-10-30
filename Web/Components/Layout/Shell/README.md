# Application shell

Responsive shared layout, navigation, authenticated actions and persisted theme.

MainLayout's paired files live in the parent Layout directory, matching its public namespace.
`MainContent.razor` and its scoped CSS define the single `main-content` landmark template used by
the mutually exclusive public and authenticated shells; routed child content retains its parent's scope.

## Contents

| File                                            | Types or asset    | Responsibility                                                                                         |
|-------------------------------------------------|-------------------|--------------------------------------------------------------------------------------------------------|
| [MainLayout.razor](../MainLayout.razor)         | `MainLayout`      | Renders the application shell and hosts navigation, content and connection recovery.                   |
| [MainLayout.razor.cs](../MainLayout.razor.cs)   | `MainLayout`      | Renders the shared application shell and checks local Docker availability after interactive rendering. |
| [MainLayout.razor.css](../MainLayout.razor.css) | Scoped stylesheet | Scoped styling for MainLayout.                                                                         |
| [NavMenu.razor](NavMenu.razor)                  | `NavMenu`         | Presents responsive navigation, authentication actions and persisted theme selection.                  |
| [NavMenu.razor.cs](NavMenu.razor.cs)            | `NavMenu`         | Renders responsive navigation and persists the selected browser theme.                                 |
| [NavMenu.razor.css](NavMenu.razor.css)          | Scoped stylesheet | Scoped styling for NavMenu.                                                                            |

## Usage and integration

MainLayout hosts the public/authenticated shell and routed content, checks local Docker availability after interactive
render and includes navigation/theme controls. NavMenu uses IDeploymentCapabilities to expose appropriate links and
browser theme functions for saved light/dark selection.

## Contracts and lifecycle

Capability-driven visibility is not authorization; server actions enforce local/cloud support. Do not contact Docker
from SaaS-only presentation paths. JS work requires an interactive mounted document, with failures handled by existing
UI guidance. Preserve keyboard/menu/focus behavior and shared theme-storage contract rather than invent a second theme
state.

## Related documentation

- [Parent module](../README.md)
- [Layout selection](../../Routes.razor)
- [Capabilities](../../../../Application/Abstractions/Hosting/README.md)
- [Theme functions](../../../wwwroot/js/README.md)
- [Shell tests](../../../../Test/Web/Presentation/ConsoleRenderingTests.cs)
