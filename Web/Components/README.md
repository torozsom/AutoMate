# Components

`Web/Components` is the presentation and interaction layer of AutoMate's
Blazor Server application. It defines the application shell, route table,
authenticated pages, reusable UI components, and the browser-facing
coordination needed to start deployments and display live runtime state.

Components resolve authentication state, call application services, queue
deployment jobs, and render DTO/entity data. They do not own deployment
execution, persistence rules, provider SDK implementation, or infrastructure
provisioning; those responsibilities remain in `Services`.

## Responsibilities

- Define the Blazor application document and interactive render modes.
- Route URLs to page components and provide the default layout.
- Render authenticated and anonymous user experiences.
- Load and display saved applications, local repositories, and GitHub
  repositories.
- Collect and validate deployment configuration before queueing work.
- Display deployment history, status, live logs, container metrics, and
  workflow links.
- Adapt server-side services and SignalR messages to user-facing UI.
- Provide shared status badges, terminal output, and reconnect behavior.
- Resolve the internal AutoMate user ID from authentication claims.

The component layer should remain an orchestration boundary for UI actions.
When a component needs new business behavior, add or reuse a service contract
instead of embedding provider or persistence logic in Razor markup.

## Structure

```text
Web/Components/
├── App.razor
├── Routes.razor
├── _Imports.razor
├── Layout/
│   ├── MainLayout.razor[.cs|.css]
│   ├── NavMenu.razor[.cs|.css]
│   ├── ReconnectModal.razor[.css|.js]
├── Pages/
│   ├── Home.razor[.css]
│   ├── LoginForm.razor[.cs|.css]
│   ├── RegistryForm.razor[.cs|.css]
│   ├── VerifyEmail.razor[.cs]
│   ├── Dashboard.razor[.cs|.css]
│   ├── GitHubRepos.razor[.cs|.css]
│   ├── LocalGitRepos.razor[.cs|.css]
│   ├── ProjectDetails.razor[.cs|.css]
│   ├── Error.razor[.cs]
│   ├── NotFound.razor
│   ├── AuthenticatedUserDetails.cs
│   ├── AuthenticatedUserResolver.cs
│   └── CloudDeploymentPageDefaults.cs
└── Shared/
    ├── ConfigurationForm.razor[.cs]
    ├── DeploymentBadge.razor
    ├── WorkflowBadge.razor
    ├── Terminal.razor
    ├── GitHubRepositoryReference.cs
    └── GitHubRepositoryUrlParser.cs
```

`*.razor` files own markup and local rendering decisions. `*.razor.cs`
partial classes own injected dependencies, lifecycle methods, event handlers,
and UI state. `*.razor.css` files contain component-scoped styles. The
exception is `app.css`, which owns global design tokens and shared primitives.

## Application Composition

### `App.razor`

`App.razor` defines the HTML document shell:

- document language, viewport, and base URL;
- Blazor resource preloading and import map;
- Bootstrap, Bootstrap Icons, application, generated, and xterm styles;
- favicon and page title;
- `HeadOutlet`, `Routes`, and reconnect modal;
- Blazor framework script;
- theme initialization;
- xterm.js, fit addon, and terminal wrapper scripts.

The asset order is significant. Theme initialization must occur after
`theme.js`; `xterm-wrapper.js` must load after xterm.js and the fit addon.
Interactive content uses `InteractiveServer`.

### `Routes.razor`

`Routes.razor` wraps the router in `CascadingAuthenticationState`, points the
router at the `Program` assembly, uses `NotFound` for missing routes, and
assigns `MainLayout` as the default layout. `FocusOnNavigate` moves focus to
the page `h1` after navigation.

Do not add authentication checks to the router itself. Pages use
`AuthorizeView`, `[AllowAnonymous]`, endpoint authorization, or service
validation according to their responsibility.

### `_Imports.razor`

The shared imports provide common Blazor forms, routing, authorization,
virtualization, JS interop, render-mode, and component namespaces. Add imports
only when they are genuinely used across the component tree; page-specific
dependencies belong in the page or code-behind.

## Layout Components

### `MainLayout`

`MainLayout` is the default authenticated application shell:

- renders a skip link to `#main-content`;
- places `NavMenu` in a sidebar;
- provides a sticky top row with Docker status and Swagger access;
- renders page content through `@Body`;
- displays the Blazor error UI with reload and dismiss affordances.

After first render it calls `IDockerService.PingAsync` so the initial page is
not blocked by a potentially slow Docker socket. The top row shows checking,
online, or offline states. A failed ping is logged as a warning and treated
as offline for presentation.

The layout is interactive-server rendered and must remain lightweight. Do not
run deployment scans, database queries, or long-running provider calls from
the layout.

### `NavMenu`

`NavMenu` renders the brand, responsive navigation, authorization-sensitive
links, logout form, and theme toggle:

| State | Navigation |
|---|---|
| Anonymous | Home and Login/Register |
| Authenticated | Dashboard, GitHub Repos, Local Repos, Logout, theme toggle |

Theme state is read from browser storage after first render through
`window.getTheme` and changed through `window.setTheme`. JS failures are
logged at debug level and do not prevent navigation.

The mobile menu uses a checkbox/toggler and closes after navigation through
the surrounding click behavior. Preserve keyboard accessibility and the
`aria-label="Primary"` navigation landmark when changing the markup.

### `ReconnectModal`

`ReconnectModal` is the framework circuit-reconnect UI. Its module JavaScript
listens for Blazor reconnect state events and manages:

- first reconnect attempt;
- repeated retry countdown state;
- retry and resume buttons;
- paused sessions;
- failed resume;
- rejected/unknown circuits, which reload the page.

The modal is intentionally implemented with the native `<dialog>` element.
The CSS exposes only the state-specific message for the current reconnect
class. Changes must preserve the element IDs and classes expected by
`ReconnectModal.razor.js`.

## Page Routes

### Public pages

| Component | Route | Purpose |
|---|---|---|
| `Home` | `/` | Public landing page and authenticated quick-action home. |
| `LoginForm` | `/login` | Local login form, OAuth entrypoint, and registration/verification messages. |
| `RegistryForm` | `/register` | Local account registration and verification-link initiation. |
| `VerifyEmail` | `/verify-email` | Email-verification prompt and token-processing page. |
| `Error` | `/Error` | Error fallback with request/activity correlation ID. |
| `NotFound` | `/not-found` | Route-not-found fallback. |

`Home` switches between an anonymous marketing/entry view and an
authenticated workspace welcome view. `LoginForm` posts credentials directly
to `/api/auth/login` with an antiforgery token; it does not implement local
authentication itself.

`RegistryForm` uses an interactive `EditForm` and `IAuthService.RegisterAsync`.
Its model validates username, email, password, and confirmation before sending
the verification-link callback. It prevents duplicate submission, navigates to
the email-check page on success, and displays user-safe failure messages.

`VerifyEmail` treats `checkemail=true` as a display-only state. Otherwise it
requires a token and calls `IAuthService.VerifyEmailAsync`; successful
verification redirects to `/login?verified=true`.

`Error` obtains a request ID from the current activity or cascading
`HttpContext`. Do not expose exception details in this page.

### Authenticated source pages

#### `Dashboard` — `/dashboard`

`Dashboard` is the primary saved-project workspace. It:

- resolves the current internal user ID;
- loads saved `Application` records;
- checks whether Azure is connected;
- displays local/remote project counts and latest deployment states;
- subscribes to `IDeploymentStatusNotifier`;
- opens `ConfigurationForm` for local or cloud deployment;
- queues `LocalDeploymentJob` or `CloudDeploymentJob`;
- deletes saved projects;
- starts Azure OAuth for organization or tenant-specific personal accounts;
- opens project details after queueing deployment.

Local projects are scanned with `IProjectScannerService.AnalyzeDependenciesAsync`
before configuration. Remote GitHub projects use
`CloudDeploymentPageDefaults` because there is no local checkout to scan.
Remote deployment is disabled until Azure is connected. Cloud queueing also
requires a parsable GitHub owner/name, Azure credentials, and a GitHub access
token.

The dashboard tracks per-project deployment state in a concurrent dictionary
to disable duplicate actions while work is being queued. Status notifications
update the latest deployment in memory and refresh the app list when no
deployment record exists yet.

#### `GitHubRepos` — `/github-repos`

`GitHubRepos` resolves the authenticated user and, for GitHub identities with
an access token, calls `IGitHubService.GetUserRepositoriesAsync`. It renders
repository metadata, privacy, language, update date, support state, and save
actions.

Repositories are considered UI-supported when their language is `C#`,
`JavaScript`, or `TypeScript`. The component does not perform deployment
compatibility analysis; it only applies the current import filter before
calling `IApplicationService.AddGitHubAppAsync`.

The component uses a cancellation source and a per-URL saving set. Dispose
cancels in-flight work, and duplicate saves for the same repository are
ignored while the first save is active.

#### `LocalGitRepos` — `/local-repos`

`LocalGitRepos` accepts a filesystem parent path and calls
`ILocalSystemScannerService.ScanForProjectsAsync`. It supports Enter-key
submission, disables scanning while active, displays scan progress and
summary counts, and lists discovered C# subprojects.

Only subprojects marked as web projects expose a Save action. Saves call
`IApplicationService.AddLocalAppAsync` with the resolved current user ID.
The component tracks saving paths independently, cancels work on disposal,
and distinguishes empty results, scan errors, and successful saves.

Filesystem paths are user input. The component passes them to the scanner;
it must not add path traversal, shell execution, or filesystem mutation logic
to the Razor layer.

#### `ProjectDetails` — `/project/{ProjectId:guid}`

`ProjectDetails` is the operational project console. It:

- loads one user-owned `Application`;
- shows source, platform, deployment, infrastructure, and configuration data;
- queues local/cloud deployment and local stop jobs;
- displays recent deployment history;
- hosts build, web, and database terminal tabs;
- receives live logs and metrics through SignalR;
- shows a local running endpoint when Docker exposes a host port;
- tracks cloud workflow status and generated workflow URL;
- updates deployment state through `IDeploymentStatusNotifier`.

On first render it creates a five-minute, data-protected token containing
`projectId:userId`, connects to `/loghub` with automatic reconnect, joins the
project group, and registers handlers for build logs, container logs, and
container metrics. Disposal leaves the group and disposes the connection.

Local database tabs are derived from scanner configuration. Terminal tabs are
kept mounted and switched with visibility styles so their xterm instances do
not need to be recreated on every tab change. Metric display accepts CPU
percentages and memory `used/limit` values, converts them to bounded progress
widths, and supports previous/next container navigation.

The page mirrors generated Docker naming when resolving the web host port and
checks both normalized and legacy container-name candidates for compatibility.
Keep this fallback aligned with `Services.Templating` and `Services.Docker`.

## Shared Components

### `ConfigurationForm`

`ConfigurationForm` is the deployment-editing modal used by the dashboard and
project details page. Its public parameters are:

| Parameter | Purpose |
|---|---|
| `Config` | Mutable `DeploymentConfigDto` being edited. |
| `ProjectPath` | Local project path used for environment-variable discovery. |
| `IsCloudDeployment` | Selects local versus Azure-specific fields and validation. |
| `OnDeployConfirmed` | Callback receiving the validated configuration. |
| `OnCancel` | Callback closing the modal without queueing work. |

It binds environment, local host port, Azure region/resource settings,
databases, and custom environment variables. Local forms can load variables
from project config files through `IProjectScannerService`; cloud forms do not
perform this scan.

On confirmation it:

- validates local ports from 1 through 65535;
- validates supported cloud database engines;
- validates connection-string names and uniqueness;
- requires database names except for Redis;
- requires valid administrator names/passwords for PostgreSQL, MySQL, and
  SQL Server;
- validates environment variable names and uniqueness;
- prevents custom variables from colliding with generated connection-string
  names;
- rebuilds `Config.CustomEnvVars` with trimmed keys and values;
- invokes `OnDeployConfirmed`.

Cloud defaults are derived from normalized project and environment names.
Existing user-edited values are preserved when the environment changes unless
they still equal the previous generated default.

The form stores a separate `EnvVarItem` list because dictionaries are
awkward for incremental Blazor binding. Do not mutate the caller's
configuration until confirmation logic has validated the editable state.

### `DeploymentBadge`

`DeploymentBadge` maps nullable `DeploymentStatus` values to a compact badge:

| Status | Text | Visual class | Icon |
|---|---|---|---|
| Starting | Starting | info | hourglass |
| Running | Running | success | play |
| Failed | Failed | danger | warning |
| Stopped | Stopped | muted | stop |
| null/other | Not Deployed | neutral | circle |

`AdditionalClasses` allows callers to extend the class list without changing
the status mapping. Status must not be communicated through color alone; the
component includes text and an icon.

### `WorkflowBadge`

`WorkflowBadge` presents cloud GitHub Actions state. It accepts nullable
status, an optional message override, and an optional workflow URL. When a URL
exists it opens the workflow in a new tab. Default messages and classes map
starting, running, failed, stopped, and no-workflow states.

Callers should provide a concise user-facing message and a trusted HTTPS
workflow URL. The component does not fetch workflow state.

### `Terminal`

`Terminal` is the Blazor adapter for `wwwroot/js/xterm-wrapper.js`. It exposes:

- `Id`, defaulting to a generated unique terminal ID;
- `OnReady`, invoked after first-render initialization;
- `WriteAsync`;
- `WriteLineAsync`.

It disposes the browser terminal through JS interop and logs expected
disconnect, cancellation, or unavailable-runtime failures at debug level.
Parents own log routing and authorization; this component only writes text.

## Authentication and Identity Resolution

`AuthenticatedUserResolver` centralizes claim-to-user resolution:

1. obtain the Blazor authentication state;
2. read `ClaimTypes.NameIdentifier`;
3. parse it as a `Guid` for local identities;
4. otherwise use `IUserService.GetUserIdByGithubAccountIdAsync` for GitHub
   provider identities;
5. return `Guid.Empty` when anonymous or unresolved.

`GetCurrentUserDetailsAsync` additionally resolves the stored access token and
whether the identity is a GitHub user. Components must use the resolver
instead of duplicating claim parsing. `Guid.Empty` is treated as anonymous or
unavailable state, not as a valid database user ID.

Never render or log access tokens. The resolver passes tokens only to the
specific service operation that requires them.

## Service Boundaries

| Component concern | Delegated dependency |
|---|---|
| Authentication state and provider-user lookup | `AuthenticationStateProvider`, `IUserService` |
| Saved application CRUD | `IApplicationService` |
| Local repository discovery | `ILocalSystemScannerService` |
| Project dependency/environment analysis | `IProjectScannerService` |
| Deployment queueing | `IDeploymentJobQueue` |
| Deployment status updates | `IDeploymentStatusNotifier` |
| Local runtime health/port lookup | `IDockerService` |
| GitHub repository access | `IGitHubService` |
| Browser state and terminal calls | `IJSRuntime` and `wwwroot/js` |
| Live logs and metrics | SignalR `/loghub`, `LogHub`, `Terminal` |
| Data protection for project-group tokens | `IDataProtectionProvider` |

Components may coordinate these calls to create a user workflow, but should
not reimplement their internal behavior.

## Lifecycle and Cancellation

Use the appropriate lifecycle stage:

- `OnInitialized` for local default state and query-parameter interpretation.
- `OnInitializedAsync` for initial service calls and subscriptions.
- `OnAfterRenderAsync(firstRender)` for browser-only JS interop, Docker pings,
  and SignalR startup.
- `Dispose` or `DisposeAsync` for event unsubscription, cancellation, JS
  resource disposal, and SignalR group/connection cleanup.

Pages that start asynchronous work own a cancellation source where needed:
`GitHubRepos` and `LocalGitRepos` cancel operations when disposed.
`ProjectDetails` disposes its SignalR connection. `Dashboard` and
`ProjectDetails` unsubscribe from `IDeploymentStatusNotifier`.

Do not call browser-only APIs during prerendering. Do not call
`StateHasChanged` from background callbacks without marshaling through
`InvokeAsync`.

## Rendering and Interaction Conventions

- Use `AuthorizeView` for state-dependent presentation and keep server-side
  authorization authoritative.
- Use clear loading, empty, error, and success states for asynchronous pages.
- Disable duplicate actions while the corresponding operation is active.
- Use status text and icons in addition to color.
- Keep external links `target="_blank"` paired with
  `rel="noopener noreferrer"` when the destination is user-controlled or
  external.
- Use `EventCallback` for child-to-parent actions.
- Stop event propagation when an action button sits inside a clickable card.
- Keep labels associated with inputs and provide `aria-live` for asynchronous
  loading states where appropriate.
- Preserve the skip link and focus-on-navigation behavior.
- Use `app.css` tokens and Bootstrap utilities before adding new global CSS.
- Put page-specific styling in the matching `.razor.css` file.

The existing pages use compact operational cards, badges, grouped forms,
responsive grids, visible focus states, and reduced-motion support. New UI
should preserve the same hierarchy and light/dark theme behavior.

## Error Handling

Components should show safe, actionable messages and log technical details on
the server:

- expected validation failures become inline warnings or form messages;
- unavailable authentication becomes an authenticated-state prompt;
- missing projects render an unavailable state rather than throwing in markup;
- service failures are logged with stable identifiers, not credentials;
- cancellation caused by component disposal is not shown as an application
  error;
- deployment queue failures reset the local busy state;
- SignalR/JS disposal failures are debug-level lifecycle noise unless they
  indicate a real user-facing problem.

Do not display raw exception text when it could expose paths, tokens,
connection strings, provider details, or stack traces. The current dashboard
and project pages include some service exception text in queue-failure
messages; preserve existing behavior unless changing it as a deliberate
security/usability fix.

## Styling and Accessibility

Global visual tokens live in `Web/wwwroot/app.css`; component-scoped CSS lives
beside the owning Razor file. Current component styles provide:

- responsive sidebar and top-row layout;
- visible skip-link focus behavior;
- theme-aware surfaces, borders, status colors, and shadows;
- compact cards and summary tiles;
- responsive source/repository grids;
- deployment console sections and terminal tabs;
- reconnect-modal state transitions;
- reduced-motion behavior through the global stylesheet.

Follow these rules when extending components:

- maintain keyboard-visible focus;
- do not communicate status only with color;
- keep headings and landmarks semantically ordered;
- preserve labels, button names, and live-region text;
- keep touch targets usable on narrow screens;
- respect `prefers-reduced-motion`;
- preserve readable contrast in both themes;
- keep spacing tiers that distinguish control groups, cards, and sections.

## Testing Guidance

Component changes should be validated through focused UI and service tests
where available, plus manual browser verification for:

- anonymous versus authenticated rendering;
- route navigation and focus placement;
- loading, empty, success, validation, and failure states;
- duplicate-save and duplicate-deployment prevention;
- component disposal during in-flight calls;
- local and cloud configuration validation;
- deployment status notifications;
- SignalR logs, metrics, reconnect, and cleanup;
- theme switching and browser refresh;
- responsive layouts and keyboard navigation;
- static asset/CDN availability.

For `ProjectDetails`, test local and remote projects separately. Verify that
local database tabs, web-port discovery, cloud workflow messages, terminal
instances, and status transitions do not leak state across project IDs.

## Extending Components

When adding a page:

1. Add the route and `PageTitle`.
2. Decide whether it is anonymous, authenticated, or both.
3. Use the default `MainLayout` unless a separate layout is required.
4. Resolve user identity through `AuthenticatedUserResolver`.
5. Inject service contracts rather than infrastructure implementations.
6. Define loading, empty, error, and success states.
7. Add cancellation and disposal for long-running work.
8. Add scoped CSS beside the component and update shared tokens only when the
   rule is genuinely global.
9. Preserve accessibility landmarks, labels, focus, and reduced motion.
10. Update this README and the project `Web/README.md` route/component map.

When adding a shared component:

1. Keep its public parameters small and explicit.
2. Use `EventCallback` for parent-owned actions.
3. Avoid hidden service calls unless the component's purpose requires them.
4. Document lifecycle, JS interop, disposal, and security assumptions.
5. Keep status mappings and formatting rules deterministic.

When changing deployment UI, trace the complete path:

```text
component input
    -> DTO/configuration validation
    -> deployment job queue
    -> Services orchestration
    -> notifier/SignalR update
    -> component state refresh
```

Do not treat a successful queue operation as a completed deployment.

## File Map

| Area | Files | Purpose |
|---|---|---|
| Application shell | `App.razor`, `Routes.razor`, `_Imports.razor` | Document shell, routing, shared imports, and interactive render setup. |
| Layout | `Layout/MainLayout.*` | Sidebar/top-row shell, Docker status, page body, and error UI. |
| Navigation | `Layout/NavMenu.*` | Auth-aware navigation, logout, responsive menu, and theme toggle. |
| Reconnect | `Layout/ReconnectModal.*` | Blazor circuit reconnect markup, styles, and retry/resume behavior. |
| Public pages | `Pages/Home.*`, `LoginForm.*`, `RegistryForm.*`, `VerifyEmail.*`, `Error.*`, `NotFound.razor` | Landing, authentication, verification, and fallback states. |
| Workspace pages | `Pages/Dashboard.*`, `GitHubRepos.*`, `LocalGitRepos.*` | Saved apps, remote repositories, and local repository scanning. |
| Operations page | `Pages/ProjectDetails.*` | Deployment console, live logs, metrics, workflow state, and configuration. |
| Configuration | `Shared/ConfigurationForm.*` | Deployment settings modal and validation. |
| Status UI | `Shared/DeploymentBadge.razor`, `WorkflowBadge.razor` | Deployment and workflow status presentation. |
| Terminal UI | `Shared/Terminal.razor` | Blazor-to-xterm adapter. |
| Identity helpers | `Pages/AuthenticatedUserDetails.cs`, `AuthenticatedUserResolver.cs` | Authentication claim and provider-user resolution. |
| Cloud defaults | `Pages/CloudDeploymentPageDefaults.cs` | Remote-project deployment defaults and minimal metadata. |
| GitHub parsing | `Shared/GitHubRepositoryReference.cs`, `GitHubRepositoryUrlParser.cs` | Owner/name extraction from persisted repository URLs. |

## Related Documentation

- [`Web`](../README.md)
- [`Web/wwwroot`](../wwwroot/README.md)
- [`Services/Orchestration`](../../Services/Orchestration/README.md)
- [`Services/LogStreaming`](../../Services/LogStreaming/README.md)
- [`Services/Scanner`](../../Services/Scanner/README.md)
- [`Services/Docker`](../../Services/Docker/README.md)
- [`Services/GitHub`](../../Services/GitHub/README.md)
- [`Core/DTO`](../../Core/DTO/README.md)
- [`Core/Entities`](../../Core/Entities/README.md)
- [`Core/Enums`](../../Core/Enums/README.md)
