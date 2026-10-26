# Blazor presentation

Document shell, route selection, layout, pages and reusable presenters.

## Contents

| File                             | Types or asset | Responsibility                                                                           |
|----------------------------------|----------------|------------------------------------------------------------------------------------------|
| [_Imports.razor](_Imports.razor) | Razor imports  | Shares component namespaces and framework imports across the Razor component tree.       |
| [App.razor](App.razor)           | `App`          | Hosts the document shell, local theme initialization and interactive Blazor entry point. |
| [Routes.razor](Routes.razor)     | `Routes`       | Resolves routed components and applies the application layout and authorization views.   |

| Module                     | Responsibility                                                              |
|----------------------------|-----------------------------------------------------------------------------|
| [Layout](Layout/README.md) | Application shell and circuit reconnect UI.                                 |
| [Pages](Pages/README.md)   | Routed workspace, repository, authentication and project views.             |
| [Shared](Shared/README.md) | Reusable analysis, configuration, console, metric and inventory presenters. |

## Usage and integration

App defines the HTML document, ordered styles/scripts and InteractiveServer entry points. Routes supplies authentication
state, layout and route-parameter keys; _Imports shares namespaces. Layout handles shell/reconnection. Pages owns
route-specific state/actions; Shared owns reusable rendering and focused presenters.

## Contracts and lifecycle

Route keys recreate project/deployment presenters when selected identities change. Async work must use
cancellation/generation fences so old responses cannot replace new route state. Marshal background notifications through
the renderer dispatcher and unsubscribe/dispose owned resources.

Independent EF-backed operations use fresh scopes. Keep concrete provider composition in Configs. Saved output must
remain visible when independent metrics fail; neither a status badge nor a successful live connection proves deployment
health. Follow leaf guides for terminal replay and JS cleanup.

## Related documentation

- [Parent module](../README.md)
- [Interactive composition](../Configs/Hosting/README.md)
- [Scoped operations/replay state](../Services/README.md)
- [Document assets](../wwwroot/README.md)
- [Rendering tests](../../Test/Web/Presentation/ConsoleRenderingTests.cs)
