# Core

`Core` is AutoMate's domain contract project.

It contains framework-neutral entities, DTOs, enums, and shared deployment contract defaults used by `Services` and
`Web`. It should remain independent from persistence, UI, Docker, GitHub, Azure, and ASP.NET-specific concerns.

---

## Responsibilities

- Define persisted domain entities.
- Define DTOs used by scanners, orchestrators, API calls, and Blazor components.
- Define enums for standardized source and deployment state.
- Define shared contract defaults that should remain consistent across callers.
- Keep shared contracts stable and infrastructure-free.

`Core` does not configure EF Core, call external APIs, render UI, or run deployments.

---

## Entity Model

### Base

| Entity       | Purpose                                          |
|--------------|--------------------------------------------------|
| `BaseEntity` | Common `Id`, `CreatedAt`, and `UpdatedAt` fields |

### Users

| Entity       | Purpose                                                         |
|--------------|-----------------------------------------------------------------|
| `User`       | Abstract user root with username, email, and owned applications |
| `LocalUser`  | Local account data, password hash, email verification state     |
| `RemoteUser` | GitHub-authenticated user with GitHub and Azure OAuth linkage   |

`RemoteUser` can store:

- GitHub account ID, avatar URL, and access token
- Azure account ID, tenant ID, subscription ID
- Azure access and refresh tokens
- Azure token expiration timestamp

Sensitive token encryption is configured in `Services`, not here.

### Projects and Deployment

| Entity          | Purpose                                                       |
|-----------------|---------------------------------------------------------------|
| `Application`   | A user-owned source project, either local or remote           |
| `CsProject`     | A discovered `.csproj` entry within an application            |
| `Configuration` | Persisted deployment configuration for a C# project           |
| `Deployment`    | Runtime deployment record and cloud/local deployment metadata |

`Deployment` tracks status, Docker image/container identifiers, cloud GitHub Actions run IDs, Container Apps revision,
and generated app URL data.

---

## DTO Map

### Project Discovery and Metadata

| DTO                   | Purpose                                                         |
|-----------------------|-----------------------------------------------------------------|
| `LocalProjectDto`     | Local repository/project discovery result                       |
| `CsProjectDto`        | Discovered C# project descriptor                                |
| `ProjectMetadataDto`  | Parsed project metadata, target framework, references, web flag |
| `GitHubRepositoryDto` | GitHub repository API payload                                   |

### Deployment Configuration

| DTO                       | Purpose                                                     |
|---------------------------|-------------------------------------------------------------|
| `DeploymentConfigDto`     | User-confirmed deployment configuration                     |
| `DatabaseConfigDto`       | Database engine, credentials, and connection-string binding |
| `TemplateFile`            | Generated deployment artifact path/content pair             |
| `TemplateManifestRuleDto` | Template manifest entry for local/cloud generation          |
| `DbProviderRuleDto`       | Database provider detection rule                            |

### Cloud Deployment

| DTO                         | Purpose                                                           |
|-----------------------------|-------------------------------------------------------------------|
| `CloudDeploymentRequestDto` | End-to-end cloud deployment orchestration request                 |
| `AzureCloudCredentialsDto`  | Connected Azure tenant/subscription/access-token data             |
| `AzureOidcSetupResultDto`   | GitHub Actions OIDC values and diagnostic Azure identity metadata |
| `GitHubWorkflowRunDto`      | GitHub Actions run status tracked by AutoMate                     |

---

## Defaults

| Type                 | Purpose                                                                                                                                                                |
|----------------------|------------------------------------------------------------------------------------------------------------------------------------------------------------------------|
| `DeploymentDefaults` | Shared deployment default values for environment names, local ports, generated database template values, Azure region, cloud deployment branch, and workflow file name |

`DeploymentDefaults.DatabasePassword` is a generated local Docker/template default, not an application secret. Real
secrets still belong in user-secrets, environment variables, or provider-specific secret stores.

---

## Enums

| Enum               | Values                                     | Purpose                                             |
|--------------------|--------------------------------------------|-----------------------------------------------------|
| `SourceType`       | `Local`, `Remote`                          | Distinguishes filesystem and GitHub-backed projects |
| `AppType`          | `WebApi`, `Blazor`, `Mvc`                  | Classifies supported .NET web project styles        |
| `DeploymentStatus` | `Starting`, `Running`, `Stopped`, `Failed` | Shared deployment lifecycle state                   |

---

## Layer Boundary

Keep `Core` intentionally small and stable:

- no EF Core fluent configuration
- no ASP.NET abstractions
- no SignalR, Docker, GitHub, Azure, Redis, or SMTP dependencies
- no service implementations
- no UI state

Changes in this project affect the full solution, so model changes should be explicit and coordinated with `Services`
and `Web`.

---

## How Other Projects Use Core

- `Services.Data` maps entities to PostgreSQL through EF Core.
- `Services.Scanner` produces project metadata DTOs.
- `Services.Templating` consumes deployment config and metadata DTOs.
- `Services.Orchestration` coordinates local and cloud deployments through shared request/status DTOs.
- `Web` uses entities and DTOs for Blazor component state, forms, and pages.
