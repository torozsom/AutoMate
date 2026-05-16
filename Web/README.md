# Web

The `Web` project is the UI and API entrypoint of AutoMate.  
It hosts the Blazor Server app, SignalR hub, and Minimal API endpoints while delegating core logic to `Services`.

---

## Responsibilities

- render interactive Blazor pages for auth, project discovery, and deployment
- configure application services, middleware, and endpoint mapping
- host SignalR hub for live build/container telemetry
- provide auth-related Minimal API endpoints

---

## Startup and configuration

- `Program.cs` is intentionally slim:
    - `builder.AddApplicationServices()`
    - `app.UseApplicationPipeline()`
    - `app.InitializeInfrastructureAsync()`

- `Configs/ServiceConfiguration.cs` registers:
    - EF Core context + Data Protection key persistence
    - Redis distributed cache
    - authentication (cookie + GitHub OAuth), authorization, rate limiting
    - Razor Components, SignalR, Swagger
    - all domain services from `Services`
    - reflection-based endpoint registration

- `Configs/AppConfiguration.cs` configures:
    - forwarded headers, exception handling/HSTS
    - status page re-execution for 404 (`/not-found`)
    - HTTPS, routing, rate limiting, auth, antiforgery
    - SignalR hub `/loghub`
    - health endpoint `/health`
    - dynamically discovered Minimal API endpoints

---

## UI module map

### `Components/Pages/`

- `Home` (`/`)  
  Marketing-style landing for anonymous users and quick actions for authenticated users.

- `LoginForm` (`/login`)  
  Local login form posting to `/api/auth/login`, plus GitHub OAuth entrypoint.

- `RegistryForm` (`/register`, component name in codebase; registration page)  
  Local registration flow with data annotations and email verification initiation.

- `VerifyEmail` (`/verify-email`)  
  Handles token verification and post-verification redirects.

- `Dashboard` (`/dashboard`)  
  Lists user projects, supports removal and deployment initiation, reacts to live status updates.

- `GitHubRepos` (`/github-repos`)  
  Fetches authenticated GitHub repositories and saves C# repositories into workspace.

- `LocalGitRepos` (`/local-repos`)  
  Scans local filesystem paths for Git/.NET projects and saves selected web projects.

- `ProjectDetails` (`/project/{ProjectId:guid}`)  
  Per-project operations: deploy/stop, live logs, live container metrics, and quick-open deployed app URL.

- `Error` (`/Error`) and `NotFound` (`/not-found`)  
  Error and fallback UX pages.

### `Components/Layout/`

- `MainLayout`  
  Shared app shell, Docker daemon status indicator, and Swagger shortcut.

- `NavMenu`  
  Auth-aware navigation + theme toggle persisted in local storage.

- `ReconnectModal`  
  Blazor circuit reconnect/resume UX.

### `Components/Shared/`

- `ConfigurationForm`  
  Deployment modal for environment/port/database/env-var configuration.
- `DeploymentBadge`  
  Compact status visualization for deployment state.
- `Terminal`  
  xterm.js wrapper component for live log streaming panels.

---

## Routing and API endpoints

`Routes/IEndpoint` is the endpoint contract. Implementations are auto-discovered and mapped:

- `StaticAssetsEndpoint` → static asset mapping
- `RazorComponentsEndpoint` → root Blazor app mapping
- `Auth/LoginEndpoint` → `POST /api/auth/login`
- `Auth/LogoutEndpoint` → `POST /api/auth/logout`
- `Auth/GitHubLoginEndpoint` → `GET /api/auth/github-login`

---

## Real-time log pipeline

- `Hubs/LogHub`  
  Clients join project groups with a short-lived, data-protected token.

- `Services/RealTimeLogStreamer`  
  Implements `ILogStreamer`, relays build logs/container logs/metrics to `project-{id}` SignalR groups.

- `ProjectDetails` subscribes to:
    - `ReceiveBuildLog`
    - `ReceiveContainerLog`
    - `ReceiveContainerMetrics`
      and renders data in dedicated terminal tabs/metric cards.

---

## Frontend assets (`wwwroot/js`)

- `theme.js`  
  Applies/persists Bootstrap theme (`light` / `dark`).

- `xterm-wrapper.js`  
  Manages xterm.js terminal instances, fit behavior, writes, and disposal.

---

## Boundary

This project should stay presentation-focused:

- UI state, routing, and request orchestration belong here
- domain/infrastructure rules remain in `Services`
