# Services

`Services` is AutoMate's application and infrastructure layer.

It implements business workflows, persistence, scanning, templating, external integrations, and deployment orchestration
on top of the domain contracts from `Core`.

---

## Responsibilities

- Persist users, applications, C# projects, configurations, and deployments with EF Core.
- Manage local, GitHub, and Azure-linked user data.
- Discover local repositories and analyze `.NET` projects.
- Render local and cloud deployment artifacts from templates.
- Run Docker Compose deployments for local projects.
- Prepare GitHub Actions based Azure Container Apps deployments for remote repositories.
- Configure Azure managed identity, OIDC federation, resource providers, and RBAC.
- Call GitHub APIs for repositories, workflow runs, logs, commits, and secrets.
- Stream deployment logs and metrics through the `ILogStreamer` abstraction.

---

## Module Map

### `Auth/`

Implements account lifecycle logic:

- local registration and login
- email verification token lifecycle
- GitHub user creation/update during OAuth
- Azure account linking after Microsoft OAuth

Main types:

- `IAuthService`
- `AuthService`

### `Data/`

EF Core and user/application persistence.

`AutoMateDbContext` configures:

- TPH user inheritance (`local`, `github`)
- encrypted GitHub and Azure OAuth token columns through Data Protection
- `User -> Application -> CsProject -> Deployment` cascade relationships
- deployment/configuration relationships
- audit timestamp updates on save

Submodules:

- `Data/Apps` - application/project persistence
- `Data/Users` - user lookup, GitHub token access, Azure credential retrieval
- `Migrations` - schema history

### `GitHub/`

GitHub API integration.

Capabilities:

- list authenticated user's repositories
- cache repository data in Redis using a hashed token cache key
- create/update GitHub repository secrets
- commit generated cloud deployment files to a deployment branch
- dispatch and poll workflow runs
- download workflow logs as zip archives and stream them back to the UI

Main types:

- `IGitHubService`
- `GitHubService`

### `Azure/`

Azure deployment preparation and runtime streaming.

Capabilities:

- create or update Azure resource groups
- create or update user-assigned managed identities
- register required Azure resource providers:
    - `Microsoft.App`
    - `Microsoft.OperationalInsights`
- configure GitHub Actions OIDC federated credentials
- assign Contributor to the workflow identity at resource group scope
- stream Azure Container Apps runtime logs/metadata after successful deployment

Important implementation notes:

- OIDC federation uses exact GitHub issuer/subject/audience matching.
- Federated credentials are created through ARM REST to preserve the issuer string exactly.
- GitHub Actions receives only repository secrets needed for OIDC login and GHCR pull.

Main types:

- `IAzureDeploymentOrchestrator`
- `AzureDeploymentOrchestrator`
- `IAzureContainerAppRuntimeStreamer`
- `AzureContainerAppRuntimeStreamer`
- `StaticAccessTokenCredential`

### `Docker/`

Local Docker integration.

Capabilities:

- check Docker daemon availability
- run Compose deployments
- stop local deployments
- stream container logs
- stream lightweight container metrics
- resolve mapped host ports for web containers

Main types:

- `IDockerService`
- `DockerService`
- `DockerOptions`

### `Scanner/`

Local project discovery and project analysis.

Capabilities:

- recursively scan local paths for Git repositories
- ignore hidden/build/system folders
- detect `.sln`, `.slnx`, and `.csproj`
- parse project metadata and project references
- detect database provider usage from `database-providers.json`
- extract environment variables from common configuration files

Main types:

- `ILocalSystemScannerService`
- `LocalSystemScannerService`
- `IProjectScannerService`
- `ProjectScannerService`

### `Templating/`

Deployment artifact generation.

Capabilities:

- read `Templates/template-manifest.json`
- render Scriban templates
- generate local Docker artifacts
- generate cloud Docker, Bicep, and GitHub Actions artifacts

Templates:

- `Dockerfile.scriban`
- `dockerignore.scriban`
- `docker-compose.scriban`
- `azure-aca.bicep.scriban`
- `github-actions.yml.scriban`

### `Orchestration/`

Deployment workflow coordination.

Local workflow:

1. load project metadata
2. apply deployment configuration
3. generate `.automate` artifacts
4. run Docker Compose
5. persist deployment state
6. start log/metric streaming

Cloud workflow:

1. prepare Azure OIDC trust and resource provider registration
2. upsert GitHub repository secrets
3. generate cloud deployment artifacts
4. commit artifacts to `automate/azure-deployment`
5. poll GitHub Actions run state
6. download workflow logs on completion
7. start Azure runtime streaming after successful deployment

Main types:

- `ILocalDeploymentOrchestrator`
- `LocalDeploymentOrchestrator`
- `ICloudDeploymentOrchestrator`
- `CloudDeploymentOrchestrator`
- `IDeploymentStatusNotifier`
- `DeploymentStatusNotifier`
- `DeploymentCleanupHostedService`

### `LogStreaming/`

Defines a presentation-independent log streaming contract.

`Web` implements this contract with SignalR.

Main type:

- `ILogStreamer`

### `Email/`

SMTP email integration for account verification.

Main types:

- `IEmailSenderService`
- `GmailSenderService`
- `EmailOptions`

---

## Design Patterns and Approaches

- **Interface-first orchestration:** external effects sit behind service interfaces.
- **Dependency inversion:** UI components depend on contracts, not concrete deployment logic.
- **Template manifest routing:** local and cloud templates are selected through metadata, not hardcoded calls.
- **Provider adapters:** GitHub, Azure, Docker, SMTP, and EF Core integrations are isolated in dedicated modules.
- **Resilient HTTP:** GitHub HTTP client is registered with standard resilience handling.
- **Encrypted token persistence:** OAuth tokens are protected with ASP.NET Core Data Protection.
- **Hosted lifecycle work:** cleanup/reconciliation uses `IHostedService`.
- **Real-time event bridge:** deployment status changes are broadcast through an in-process notifier and streamed
  through SignalR.

---

## Cloud Deployment Security Model

AutoMate does not store Azure client secrets for deployment.

Instead:

- the user connects Azure with Microsoft OAuth
- AutoMate uses the delegated Azure token to prepare resources
- GitHub Actions authenticates to Azure with OIDC
- an Azure user-assigned managed identity receives scoped Contributor access
- generated GitHub repository secrets contain OIDC login IDs and GHCR credentials

This avoids long-lived Azure service principal secrets in GitHub repositories.

---

## Contributor Notes

- Keep orchestration logic in this project, not in Blazor components.
- Keep all external API clients behind interfaces.
- Add new deployment templates through `Templating/Templates` and `template-manifest.json`.
- Update `Core` only when shared contracts actually change.
- Do not add UI-specific dependencies to `Services`.
- Do not spawn manual background timer threads; use hosted services.
