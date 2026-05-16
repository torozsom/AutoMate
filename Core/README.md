# Core

The `Core` project is the domain contract of AutoMate.  
It contains the shared model used by both `Services` and `Web`, with no dependencies on those outer layers.

---

## Responsibilities

- define **domain entities** persisted by EF Core in the Services layer
- define **DTOs** used for data exchange between services and UI
- define **enums** that standardize source and deployment state

`Core` does **not** implement infrastructure, HTTP endpoints, Docker operations, or UI behavior.

---

## Directory map

### `Entities/`

Domain model classes:

- `BaseEntity`  
  Common base with `Id`, `CreatedAt`, `UpdatedAt`.

- `User` hierarchy
    - `LocalUser`: password hash + email verification token/expiry
    - `GitHubUser`: external account ID, avatar URL, GitHub access token

- Project model
    - `Project`: owner, source type/path, app type, child C# projects
    - `CsProject`: individual `.csproj` unit, web project flag, deployment history
    - `LocalProjectConfig`: deployment defaults (port, db requirement, visibility, env var JSON)

- Deployment model
    - `Deployment`: runtime status and Docker identity (`ImageTag`, `DockerContainerId`)

### `DTO/`

Transport records/classes used by scanner/orchestration/UI flows:

- `LocalProjectDto`, `CsProjectDto` for scanner results
- `ProjectMetadataDto` for parsed `.csproj` graph metadata
- `DeploymentConfigDto` and `DatabaseConfigDto` for deployment setup
- `GitHubRepositoryDto` for GitHub API repository payloads
- `DbProviderRuleDto`, `TemplateManifestRuleDto` for scanner/template config

### `Enums/`

- `SourceType`: `Local`, `Remote`
- `AppType`: `WebApi`, `Blazor`, `Mvc`
- `DeploymentStatus`: `Starting`, `Running`, `Stopped`, `Failed`

---

## How Core is used in the solution

- `Services` maps entities to PostgreSQL with EF Core.
- `Services.Scanner` produces DTOs defined here.
- `Services.Orchestration` consumes deployment DTOs/status enums.
- `Web` uses entities/DTOs for UI rendering and component state.

---

## Design boundary

Keep `Core` stable and framework-neutral:

- no ASP.NET, EF configuration, Docker, or external API clients
- model-first changes only (state + contracts)
- changes in `Core` should be intentional because they affect all other projects
