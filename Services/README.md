# Services

The `Services` project is the application backend layer of AutoMate.  
It implements business workflows, infrastructure adapters, and deployment orchestration on top of `Core`.

---

## Responsibilities

- persist users/projects/deployments in PostgreSQL via EF Core
- handle local + GitHub user auth flows and email verification
- scan local repositories and analyze `.csproj` dependency graphs
- generate deployment artifacts from templates
- run Docker Compose operations and stream runtime telemetry
- expose orchestration services consumed by the Blazor UI

---

## Module map

### `Auth/`

- `IAuthService`, `AuthService`
- Local registration and login
- Email verification token lifecycle
- GitHub user creation/update during OAuth ticket processing

### `Data/`

- `AutoMateDbContext` with:
    - user TPH discriminator (`local` / `github`)
    - encrypted `GitHubUser.AccessToken` via Data Protection
    - cascade relationships (`User -> Project -> CsProject -> Deployment`)
    - audit timestamp updates on save
- `Projects/ProjectService`: save/list/delete local and GitHub project entries
- `Users/UserService`: resolve internal IDs and GitHub-linked user context

### `GitHub/`

- `GitHubService` calls GitHub `user/repos`
- distributed cache (Redis) keyed by hashed access token
- refresh-on-demand support (`forceRefresh`)

### `Email/`

- `GmailSenderService` (MailKit SMTP)
- used by registration verification flow
- configured through `EmailOptions`

### `Scanner/`

- `LocalSystemScannerService`:
    - recursively scans paths for Git repos
    - skips symlinks/hidden/build output folders
    - detects `.sln`, `.slnx`, `.csproj`
- `ProjectScannerService`:
    - parses `.csproj` + project references recursively
    - detects database dependencies from `database-providers.json`
    - extracts env vars from config files (`appsettings`, `launchSettings`, `.env`)

### `Templating/`

- `TemplatingService` with Scriban templates
- reads `Templates/template-manifest.json`
- generates:
    - `Dockerfile`
    - `Dockerfile.dockerignore`
    - `docker-compose.yml`

### `Docker/`

- `DockerService` with Docker.DotNet + `docker` CLI integration
- ping daemon, compose up/down, running project detection
- container log and metrics streaming
- host port lookup for running web containers

### `Orchestration/`

- `LocalDeploymentOrchestrator`:
    1. locate solution root
    2. scan metadata/dependencies
    3. generate deployment files under `.automate/`
    4. run `docker compose up`
    5. persist status transitions and start stream tasks
- `DeploymentStatusNotifier`: in-process status event bridge
- `DeploymentCleanupHostedService`: startup sync of deployment status with actual Docker state

### `LogStreaming/`

- `ILogStreamer` abstraction for real-time log/metric publishing
- implemented in `Web` by SignalR-backed `RealTimeLogStreamer`

### `Migrations/`

- EF Core migration history for schema evolution

---

## End-to-end deployment flow (local project)

1. UI requests deployment for a selected local web project.
2. Scanner analyzes project metadata and inferred DB dependencies.
3. User confirms generated config (ports, DB entries, env vars).
4. Templating writes deployment files into `.automate`.
5. Docker service runs compose and streams build/container telemetry.
6. Deployment status is updated and broadcast to UI subscribers.

---

## Notes for contributors

- Interfaces are first-class contracts; keep implementations behind DI boundaries.
- Treat scanner/template behavior as part of deployment compatibility.
- Avoid coupling this layer to Blazor component concerns.
