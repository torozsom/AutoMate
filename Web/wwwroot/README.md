# wwwroot

`Web/wwwroot` contains the browser-delivered static assets for the AutoMate
Blazor Server application. It supplies the global visual system, vendored
Bootstrap assets, JavaScript interop helpers, and the application favicon.

The folder is intentionally presentation-only. It must not contain business
logic, deployment orchestration, authentication decisions, persistence code,
or server-side secrets.

## Responsibilities

- Provide global CSS and design tokens through `app.css`.
- Provide Bootstrap 5.3.3 CSS and JavaScript under `lib/bootstrap`.
- Persist and apply the user's light/dark theme through `js/theme.js`.
- Manage xterm.js terminal instances through `js/xterm-wrapper.js`.
- Provide the AutoMate favicon.
- Serve assets through ASP.NET Core static-asset mapping.

The application shell is defined in `Components/App.razor`. It references
these assets and also loads Bootstrap Icons and xterm.js from jsDelivr.

## Asset Structure

```text
Web/wwwroot/
├── README.md
├── app.css
├── favicon.png
├── js/
│   ├── theme.js
│   └── xterm-wrapper.js
└── lib/
    └── bootstrap/
        └── dist/
            ├── css/
            └── js/
```

### `app.css`

The application stylesheet overrides Bootstrap defaults and defines the
AutoMate visual language:

- light and dark color tokens;
- typography and monospace font stacks;
- surface, border, status, focus, radius, and shadow tokens;
- page headers, cards, empty states, dashboards, banners, and console panels;
- buttons, badges, form controls, validation, and focus states;
- deployment status and workflow status styling;
- deployment configuration modal styling;
- responsive workflow-banner behavior;
- reduced-motion handling.

The stylesheet uses `--am-*` custom properties for application-specific
values and maps relevant values into Bootstrap `--bs-*` variables. Components
should prefer these existing tokens and Bootstrap utilities over introducing
one-off colors or dimensions.

### `js/theme.js`

This file exposes three global functions consumed through Blazor JavaScript
interop:

| Function | Behavior |
|---|---|
| `window.setTheme(theme)` | Sets `data-bs-theme` on `<html>` and stores the value in `localStorage` under `theme`. |
| `window.getTheme()` | Returns the stored theme or `light` when no value exists. |
| `window.initializeTheme()` | Reads the stored theme and applies it to `<html>`. |

`App.razor` loads the script and calls `initializeTheme()` before the
interactive application begins. `Layout/NavMenu.razor.cs` reads the current
value after first render and invokes `setTheme` when the user toggles the
theme.

Supported values are currently `light` and `dark`. The CSS selector and
Bootstrap integration depend on the `data-bs-theme` attribute being applied
to the document element; changing the storage key or attribute requires
coordinated changes in the script, component, and stylesheet.

Theme persistence is browser-local. It is not stored in the AutoMate database
and is not synchronized between browsers or users.

### `js/xterm-wrapper.js`

This file exposes `window.xtermWrapper`, a registry for multiple xterm.js
instances:

| Method | Behavior |
|---|---|
| `init(elementId)` | Creates a terminal, loads `FitAddon`, opens it in the element, fits it, and observes container resizing. |
| `write(elementId, data)` | Writes data when the registered terminal exists. |
| `dispose(elementId)` | Disconnects the resize observer, disposes the terminal, and removes it from the registry. |

Each registry entry contains the xterm instance, fit addon, and
`ResizeObserver`. The wrapper expects the global `Terminal` and
`FitAddon.FitAddon` constructors to have been loaded first.

`Components/Shared/Terminal.razor` is the server-side adapter. It:

1. renders the target element;
2. calls `xtermWrapper.init` after the first render;
3. exposes `WriteAsync` and `WriteLineAsync` methods to parent components;
4. calls `xtermWrapper.dispose` when the component is disposed;
5. logs expected JS interop shutdown failures at debug level.

`Components/Pages/ProjectDetails.razor` uses this component for live build and
container output. The browser wrapper does not connect to the log stream
itself; SignalR and server-side services deliver data to the component, which
then writes it through JS interop.

The wrapper assumes that `elementId` is unique for the lifetime of the page.
Use the component's generated default ID or provide a stable unique ID when
rendering multiple terminals.

### `lib/bootstrap`

Bootstrap 5.3.3 is checked into the repository under `lib/bootstrap/dist`.
The distribution includes:

- full, minified, and RTL CSS;
- grid, reboot, and utility CSS subsets;
- full and minified JavaScript;
- ES module and bundle variants;
- source maps.

The current application shell references:

```text
lib/bootstrap/dist/css/bootstrap.min.css
```

Bootstrap JavaScript is present locally, but `App.razor` currently does not
load the local Bootstrap script. Do not assume Bootstrap's JavaScript
components are active merely because the files exist.

### `favicon.png`

The PNG is referenced by `Components/App.razor` as the application favicon.
Keep it suitable for browser tab and bookmark use; it is not part of the
application's runtime domain model.

## External Browser Dependencies

`Components/App.razor` currently loads these external assets from jsDelivr:

| Asset | Use |
|---|---|
| Bootstrap Icons 1.11.3 CSS | Navigation and status icons via `bi` classes. |
| xterm.js 5.3.0 CSS | Terminal appearance. |
| xterm.js 5.3.0 JavaScript | Browser terminal implementation. |
| xterm-addon-fit 0.8.0 JavaScript | Terminal sizing and resize fitting. |

The local `wwwroot` files do not vendor these CDN dependencies. Changes to
versions, CDN URLs, integrity policy, or offline support belong in
`Components/App.razor` and should be reviewed together with this folder.

Bootstrap Icons are consumed by components such as `Layout/NavMenu.razor`.
The icon classes are presentation conventions, not server-side contracts.

## Static Asset Serving

`Routes/Endpoints/StaticAssetsEndpoint.cs` maps static assets with:

```csharp
app.MapStaticAssets().AllowAnonymous();
```

The endpoint is registered before other endpoints so framework and application
assets can be served efficiently without requiring authentication. Static
asset access is therefore intentionally public. Never place credentials,
tokens, private configuration, generated deployment secrets, or user data
under `wwwroot`.

`App.razor` uses the `@Assets[...]` asset manifest helper for local files such
as Bootstrap CSS and `app.css`. Direct paths are used for `favicon.png` and
the custom JavaScript files. Preserve the existing path style when changing
references so static asset fingerprinting and framework asset handling remain
consistent.

## Runtime Loading Order

The application shell loads assets in this order:

1. Bootstrap CSS.
2. Bootstrap Icons CSS from the CDN.
3. `app.css`.
4. generated `Web.styles.css`.
5. xterm.js CSS from the CDN.
6. Blazor framework JavaScript.
7. `theme.js`.
8. immediate `initializeTheme()` call.
9. xterm.js JavaScript from the CDN.
10. xterm-fit JavaScript from the CDN.
11. `xterm-wrapper.js`.

The order is significant:

- `app.css` must follow Bootstrap CSS so application tokens and component
  overrides win.
- `theme.js` must load before `initializeTheme()` is called.
- xterm.js and its fit addon must load before `xterm-wrapper.js`.
- Blazor must be available before interactive components use JS interop.

If a script is moved or deferred, verify first render, theme initialization,
terminal initialization, and reconnect/disposal behavior.

## Styling Conventions

Use the existing visual system when adding UI:

- use `--am-*` variables for AutoMate-specific colors, surfaces, borders,
  shadows, and typography;
- use Bootstrap layout and utility classes for standard spacing and grids;
- use `var(--am-text-muted)` and `var(--am-text-subtle)` rather than fixed
  gray values for secondary content;
- preserve both `[data-bs-theme="light"]` defaults and dark-mode behavior;
- use the existing `:focus-visible` treatment for keyboard access;
- retain `prefers-reduced-motion` behavior for new transitions;
- use existing status classes for deployment and workflow states;
- keep interactive targets at the existing button/form-control sizing.

New component-specific CSS belongs beside the component as a scoped
`.razor.css` file when it is not a global visual rule. Add rules to
`app.css` only when the behavior is shared across pages or components.

Avoid:

- hard-coded colors that bypass theme tokens;
- selectors that depend on generated Blazor scope attributes;
- global element rules for a single page;
- animation without reduced-motion handling;
- styling that conveys status through color alone;
- adding a second design-token system.

## JavaScript Interop Conventions

JavaScript helpers in this folder are global browser adapters, not general
application services. Keep them:

- small and deterministic;
- safe when an element or registry entry is absent;
- explicit about lifecycle ownership;
- compatible with Blazor Server reconnect and disposal behavior;
- free of server-side business decisions.

When adding a helper:

1. define a stable `window` namespace/function contract;
2. load the script in `Components/App.razor` after its dependencies;
3. call it through `IJSRuntime` from a component or presentation service;
4. handle prerender/first-render and disconnected-circuit behavior;
5. dispose observers, timers, and browser resources;
6. document the contract here.

Do not expose secrets or trust browser-provided values as authorization input.
Browser storage and DOM state are user-controlled presentation state.

## Accessibility and Responsive Behavior

The global stylesheet includes:

- visible custom focus rings;
- readable light/dark contrast tokens;
- responsive workflow banners;
- reduced-motion handling;
- semantic link and form-control styling.

New assets and styles should preserve keyboard navigation, focus visibility,
screen-reader semantics supplied by the Razor markup, and adequate contrast
in both themes. Do not rely only on hover, color, or terminal output to
communicate important state.

The terminal is an output surface. It should remain readable and resize with
its container, but it is not a replacement for accessible status text or
workflow controls.

## Security and Privacy

- Treat every static asset as publicly readable.
- Do not put secrets, access tokens, connection strings, or private user data
  in this folder.
- Keep deployment and authentication logic in server-side Web/Services code.
- Do not use local storage for credentials or security decisions.
- Review CDN additions for version pinning, availability, and supply-chain
  implications.
- Avoid logging sensitive terminal content from browser helpers.
- Keep terminal cleanup in place to prevent stale instances and observer
  leaks.

The terminal may display build output or provider messages. The server-side
log-streaming and authorization boundaries remain responsible for deciding
which project data reaches the component.

## Testing and Validation

For changes in `wwwroot`, validate the browser behavior rather than only
compilation:

- verify static assets load without authentication;
- verify the application shell loads CSS and scripts in the expected order;
- verify theme preference is applied before/at initial interactive render;
- verify theme changes survive a page refresh;
- verify light and dark colors, focus states, and responsive layouts;
- render multiple terminals and confirm independent writes/disposal;
- resize terminal containers and confirm `FitAddon` recalculates dimensions;
- navigate away from a terminal page and confirm observers and terminals are
  disposed;
- test Blazor reconnect/disconnected-circuit disposal behavior;
- check CDN failures and decide whether the feature needs a local fallback;
- run `git diff --check` after editing text assets.

For CSS changes, inspect affected pages including the dashboard, project
details, authentication forms, navigation, deployment modal, workflow banner,
and terminal panel. For JavaScript changes, test both first render and
subsequent interactive updates.

## Extending the Asset Module

When adding an asset:

1. Decide whether it is global (`wwwroot`) or component-scoped
   (`.razor.css`/component JavaScript).
2. Add it under a focused subdirectory such as `js/` or `lib/`.
3. Reference it from `Components/App.razor` or the owning component.
4. Preserve dependency and initialization order.
5. Keep browser state separate from server authorization and business state.
6. Add cleanup for observers, event handlers, timers, and third-party
   instances.
7. Support both themes and reduced motion where applicable.
8. Update this README when the runtime contract or asset inventory changes.

Do not add generated build output, package caches, local environment files,
or deployment artifacts to this directory.

## File Map

| File or directory | Purpose |
|---|---|
| `app.css` | Global AutoMate design tokens, Bootstrap overrides, component primitives, responsive rules, and accessibility states. |
| `js/theme.js` | Browser-local light/dark theme persistence and application. |
| `js/xterm-wrapper.js` | xterm.js instance registry, writing, fitting, and disposal. |
| `lib/bootstrap/dist/css` | Vendored Bootstrap 5.3.3 styles and source maps. |
| `lib/bootstrap/dist/js` | Vendored Bootstrap 5.3.3 scripts and source maps. |
| `favicon.png` | Browser favicon. |

## Related Documentation

- [`Web`](../README.md)
- `Web/Components` when component-level documentation is added
- [`Services/LogStreaming`](../../Services/LogStreaming/README.md)
- [`Services/Orchestration`](../../Services/Orchestration/README.md)
