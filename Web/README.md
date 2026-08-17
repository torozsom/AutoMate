# Web

`Web` is AutoMate's Blazor Server UI and HTTP entrypoint.

It hosts the interactive application shell, Minimal API endpoints, SignalR hub, authentication pipeline, and startup
configuration while delegating business logic to `Services`.

---

## Responsibilities

- Render the dashboard, project discovery pages, deployment forms, and live project details.
- Host cookie, GitHub OAuth, and Microsoft Entra OAuth authentication flows.
- Provide Minimal API endpoints for auth and app bootstrapping.
- Configure dependency injection and the ASP.NET Core request pipeline.
- Host SignalR `/loghub` for real-time build, container, and metric streaming.
- Keep `Program.cs` thin and move setup into configuration extensions.

---

## Startup Structure

`Program.cs` intentionally stays minimal:

```csharp
builder.AddApplicationServices();
app.UseApplicationPipeline();
app.InitializeInfrastructureAsync();
```

### `Configs/ServiceConfiguration.cs`

Registers application services:

- logging and health checks
- strongly typed options
- PostgreSQL EF Core context
- Redis distributed cache
- Data Protection key persistence
- cookie authentication
- GitHub OAuth
- Microsoft OAuth for Azure connection
- authorization and rate limiting
- Razor Components and SignalR
- Swagger/OpenAPI
- domain services from `Services`
- Minimal API endpoint discovery

### `Configs/AppConfiguration.cs`

Configures runtime middleware:

- forwarded headers
- exception handling and HSTS
- HTTPS redirection
- static assets
- routing
- rate limiting
- authentication and authorization
- antiforgery
- SignalR hub mapping
- health endpoint
- dynamically discovered endpoints

---

## UI Pages

| Page             | Route                       | Purpose                                                    |
|------------------|-----------------------------|------------------------------------------------------------|
| `Home`           | `/`                         | Landing and authenticated quick actions                    |
| `LoginForm`      | `/login`                    | Local login plus OAuth entrypoints                         |
| `RegistryForm`   | `/register`                 | Local registration                                         |
| `VerifyEmail`    | `/verify-email`             | Email verification callback                                |
| `Dashboard`      | `/dashboard`                | Saved projects, deployment actions, Azure connection state |
| `GitHubRepos`    | `/github-repos`             | GitHub repository import                                   |
| `LocalGitRepos`  | `/local-repos`              | Local repository scan/import                               |
| `ProjectDetails` | `/project/{ProjectId:guid}` | Deployment controls, live logs, metrics, workflow status   |
| `Error`          | `/Error`                    | Error fallback                                             |
| `NotFound`       | `/not-found`                | 404 fallback                                               |

---

## Shared Components

| Component           | Purpose                                      |
|---------------------|----------------------------------------------|
| `ConfigurationForm` | Local/cloud deployment configuration modal   |
| `DeploymentBadge`   | Compact deployment status display            |
| `WorkflowBadge`     | GitHub Actions/cloud workflow status display |
| `Terminal`          | xterm.js-backed live terminal panel          |

`ConfigurationForm` supports:

- environment selection
- local exposed ports
- cloud Azure region/resource settings
- database entries
- custom environment variables
- local config-file environment variable discovery

---

## Layout Components

| Component        | Purpose                                      |
|------------------|----------------------------------------------|
| `MainLayout`     | Application shell and common layout concerns |
| `NavMenu`        | Auth-aware navigation and theme control      |
| `ReconnectModal` | Blazor circuit reconnect UX                  |

---

## Minimal API Endpoints

Routes are not defined directly in `Program.cs`.

Endpoint classes implement `Routes/IEndpoint` and are mapped through endpoint discovery.

Current endpoints:

| Endpoint                   | Route                        | Purpose                                        |
|----------------------------|------------------------------|------------------------------------------------|
| `StaticAssetsEndpoint`     | framework static assets      | static file asset mapping                      |
| `RazorComponentsEndpoint`  | root app                     | Blazor app mapping                             |
| `Auth/LoginEndpoint`       | `POST /api/auth/login`       | local login                                    |
| `Auth/LogoutEndpoint`      | `POST /api/auth/logout`      | logout                                         |
| `Auth/GitHubLoginEndpoint` | `GET /api/auth/github-login` | GitHub OAuth challenge                         |
| `Auth/AzureLoginEndpoint`  | `GET /api/auth/azure-login`  | Microsoft OAuth challenge for Azure connection |

This keeps routing modular and consistent with the Clean Architecture boundary.

---

## Authentication Flows

### Local Accounts

- Registration stores a `LocalUser`.
- Email verification is required before login.
- Passwords are hashed through ASP.NET Core Identity primitives.

### GitHub OAuth

- GitHub OAuth creates or updates a `RemoteUser`.
- GitHub access tokens are encrypted at rest by the `Services` data layer.
- GitHub tokens are used to import repositories and prepare cloud deployment branches/secrets.

### Azure Connection

- The user starts Azure connection from the dashboard.
- Microsoft OAuth links Azure account metadata to the current `RemoteUser`.
- AutoMate stores tenant, subscription, access token, refresh token, and expiration metadata.
- Cloud deployment requires both GitHub and Azure connection.

---

## Real-Time Streaming

### SignalR Hub

`Hubs/LogHub` exposes project-scoped groups.

Clients join groups using a short-lived, data-protected token containing:

```text
projectId:userId
```

### Streamed Messages

`ProjectDetails` subscribes to:

- `ReceiveBuildLog`
- `ReceiveContainerLog`
- `ReceiveContainerMetrics`

`Services` publishes through `ILogStreamer`; `Web.Services.RealTimeLogStreamer` bridges that contract to SignalR.

---

## Frontend Assets

| Asset                         | Purpose                                            |
|-------------------------------|----------------------------------------------------|
| `wwwroot/js/theme.js`         | Bootstrap theme persistence                        |
| `wwwroot/js/xterm-wrapper.js` | xterm.js setup, fit handling, writes, and disposal |

The frontend uses Bootstrap 5 and Bootstrap Icons to keep the UI compact and operational rather than marketing-heavy.

---

## Presentation Boundary

Keep this project focused on presentation and request orchestration:

- Blazor state and UI belongs here.
- HTTP route definitions belong in endpoint classes.
- Business workflows belong in `Services`.
- Shared model contracts belong in `Core`.
- Do not put Docker, GitHub, Azure, or EF Core implementation logic in components.

---

## Configuration Notes

Common local configuration is supplied through `appsettings.Development.json`, environment variables, or user-secrets.

Do not commit real secrets.

Required categories:

- connection strings
- GitHub OAuth credentials
- Microsoft OAuth credentials
- email sender credentials
- Docker settings, when customized

Database migrations are executed from `Web` while targeting `Services`:

```bash
cd Web
dotnet ef migrations add <MigrationName> --project ../Services
dotnet ef database update --project ../Services
```
