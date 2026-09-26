# Core Entities

`Core/Entities` contains AutoMate's persisted domain entities. The classes
describe ownership, source projects, deployment configuration, deployment
history, and local or remote user identity.

The entities are deliberately framework-neutral. They do not contain EF Core
configuration, database queries, authentication workflows, Docker behavior,
provider SDK calls, or UI state. Persistence rules are applied by
`Services.Data.AutoMateDbContext`.

## Aggregate Model

```text
User
└── Application
    └── CsProject
        ├── Configuration       (zero or one)
        └── Deployment          (zero or many)
```

Ownership is enforced by foreign keys and cascade delete in the data layer:

| Relationship | Cardinality | Delete behavior |
|---|---:|---|
| `User -> Application` | one-to-many | Deleting a user deletes owned applications. |
| `Application -> CsProject` | one-to-many | Deleting an application deletes its C# projects. |
| `CsProject -> Configuration` | one-to-one | Deleting a C# project deletes its configuration. |
| `CsProject -> Deployment` | one-to-many | Deleting a C# project deletes deployment history. |

The aggregate root for saved project/deployment data is effectively
`Application`, while `User` owns the application collection. Services should
load and modify related entities through the appropriate aggregate boundary
instead of constructing disconnected graphs with guessed IDs.

## Entity Map

### `BaseEntity`

Provides the shared persistence identity and audit fields:

- `Id`: generated as a `Guid` when the object is constructed.
- `CreatedAt`: populated by `AutoMateDbContext` when the entity is first
  saved.
- `UpdatedAt`: refreshed by `AutoMateDbContext` when an entity is added or
  modified.

Callers should not treat `CreatedAt` or `UpdatedAt` as reliable before the
entity has been persisted. The timestamps are UTC `DateTimeOffset` values
assigned in `SaveChanges` and `SaveChangesAsync`.

### `User`, `LocalUser`, and `RemoteUser`

`User` is the abstract common identity containing required `Username`,
required `Email`, and owned `Applications`.

`LocalUser` represents email/password authentication:

- `PasswordHash` stores a hash, never a plaintext password.
- `IsEmailVerified` gates local login.
- `EmailVerificationToken` and `VerificationTokenExpiry` support the
  single-use email-verification flow.

`RemoteUser` represents a GitHub-authenticated account and its optional Azure
connection:

- `AccountId`, `Username`, and `Email` identify the remote profile.
- `AvatarUrl` and `GitHubAccessToken` store GitHub profile/access data.
- `AzureAccountId`, `AzureTenantId`, `AzureSubscriptionId`,
  `AzureAccessToken`, `AzureRefreshToken`, and `AzureTokenExpiresAt` represent
  the linked Microsoft Entra/Azure connection.

EF Core stores both derived types in the `users` table using table-per-type
hierarchy (TPH) mapping with the discriminator values `local` and `github`.
The base email is unique across both account types. A remote user is the
required identity type for GitHub repository access and Azure linking.

### `Application`

Represents a user-owned source repository or project container:

- `UserId` and `User` identify the owner.
- `Name` is the AutoMate display/project name.
- `SourceType` distinguishes `Local` and `Remote`.
- `SourcePathOrUrl` contains a local filesystem path for local sources or a
  repository URL for remote sources.
- `AppType` classifies the application as `WebApi`, `Blazor`, or `Mvc`.
- `CsProjects` contains discovered or resolved C# projects.

The application is the link between an imported source and the individual
`.csproj` records that can be analyzed or deployed. `Services.Data.Apps`
prevents duplicate local applications by user/source path and duplicate
remote applications by user/source URL.

### `CsProject`

Represents one C# project within an `Application`:

- `AppId` and `Application` identify the owning application.
- `Name` is the project name.
- `Path` is the `.csproj` path for local projects or the repository-root
  anchor used for resolved remote cloud projects.
- `IsWebProject` marks projects eligible for the web deployment workflow.
- `Configuration` is the optional persisted deployment configuration.
- `Deployments` contains deployment records ordered and interpreted by
  orchestration code.

Local project imports create a default `Configuration` for a discovered
project. Cloud deployment can resolve an existing project by
`CsProjectId`, select an existing web project, or create a default web
`CsProject` for a remote application.

### `Configuration`

Stores persisted project-level deployment settings:

#### General/local settings

- `CsProjectId`: the unique one-to-one owner.
- `DotNetVersion`: target .NET version used for generated deployment assets.
- `LocalExposedPort`: optional local host port.
- `RequiresDb`: whether the project requires a database.
- `IsPublic`: whether the local deployment is publicly accessible rather than
  restricted to localhost.
- `EnvironmentVariablesJson`: serialized environment-variable configuration.

#### Cloud settings

- `CloudAzureRegion`
- `CloudRegistryName`
- `CloudResourceGroupName`
- `CloudContainerAppName`

This entity is the persisted project configuration model. It is distinct from
`Core.DTO.DeploymentConfigDto`, which is the richer mutable request/configuration
contract used by the deployment UI and orchestration pipeline. Services are
responsible for mapping between them.

### `Deployment`

Records one local or cloud deployment attempt for a `CsProject`:

- `CsProjectId` and `CsProject` identify the deployed project.
- `Status` records the shared lifecycle state from `DeploymentStatus`.
- `ImageTag` identifies the generated Docker image or, for cloud workflows,
  may contain the commit SHA used by the workflow.
- `DockerContainerId` identifies the local Docker container when applicable.
- `CloudGitHubActionRunId` identifies the GitHub Actions run for cloud
  deployment.
- `CloudAppUrl` stores the resulting cloud application URL when available.
- `CloudContainerRevision` stores the deployed Azure Container Apps revision
  when available.

The local orchestrator creates a deployment before scanning, templating, and
starting Docker Compose. The cloud orchestrator creates one with
`DeploymentStatus.Starting`, then records workflow and cloud identifiers as
the deployment progresses. A deployment is history, not the configuration
itself; multiple deployments may belong to one `CsProject`.

## Persistence Rules Owned by `Services.Data`

The entities do not enforce these rules themselves. Do not duplicate them in
constructors or property setters without coordinating with the data layer.

- `User.Email` is required, maximum 255 characters, and unique.
- `User.Username` is required, maximum 100 characters.
- `Application.Name` is required, maximum 200 characters.
- `Application.UserId`, `CsProject.AppId`, `Configuration.CsProjectId`, and
  `Deployment.CsProjectId` are required relationships in the database.
- `Configuration.CsProjectId` has a unique index, enforcing one configuration
  per C# project.
- `User` uses TPH inheritance with `local` and `github` discriminator values.
- GitHub and Azure OAuth token properties are protected with ASP.NET Core Data
  Protection before persistence.
- Audit timestamps are assigned during `SaveChanges` and
  `SaveChangesAsync`.
- PostgreSQL maps `Guid` IDs to UUIDs and audit timestamps to
  `timestamp with time zone`.

For persistence changes, update `AutoMateDbContext`, create a migration from
the `Web` startup project targeting `Services`, and verify the resulting
schema. Do not modify generated migration snapshots manually as a substitute
for a migration.

## Security and Sensitive Properties

The following entity properties contain secrets or authentication material:

- `LocalUser.PasswordHash`
- `LocalUser.EmailVerificationToken`
- `RemoteUser.GitHubAccessToken`
- `RemoteUser.AzureAccessToken`
- `RemoteUser.AzureRefreshToken`

Rules:

- Never log full entity instances or these properties.
- Never expose token or password fields to UI models or API responses.
- Passwords must be processed through the configured password hasher.
- OAuth tokens are encrypted by the EF Core value converters in
  `AutoMateDbContext`; the entity layer itself does not encrypt them.
- Verification tokens are temporary and are cleared after successful
  verification.

## Lifecycle and Status

`Deployment.Status` uses `DeploymentStatus`:

```text
Starting -> Running
Starting -> Failed
Running  -> Stopped
Running  -> Failed
```

The enum's default value is `Starting`, so new `Deployment` instances created
without an explicit status begin in that state. Orchestration code is
responsible for valid transitions, persistence, notifications, and cleanup;
the entity does not implement a state machine.

User lifecycle is handled by `Services.Auth`:

- local registration creates an unverified `LocalUser` with a hashed password
  and expiring verification token;
- successful verification sets `IsEmailVerified` and clears token data;
- GitHub sign-in creates or updates a `RemoteUser`;
- Azure OAuth updates the Azure connection fields on an existing
  `RemoteUser`.

## Usage Guidance

Use entities when working with persisted domain state:

- `Services.Data.Users` queries `User` and projects only the credentials needed
  for Azure or GitHub operations.
- `Services.Data.Apps` creates applications and C# projects from discovery
  DTOs, loads project/deployment graphs for the UI, and enforces ownership
  checks.
- `Services.Orchestration` creates and updates `Deployment` records.
- `Web` displays loaded entity graphs but should delegate persistence and
  workflow changes to services.

Prefer no-tracking queries for read-only UI or lookup data. Load navigation
properties explicitly when the caller needs them; do not assume collections
are populated merely because the entity exposes them.

## Extending the Entity Model

Before adding or changing an entity property:

1. Decide whether the value is persisted domain state or belongs in a DTO,
   options class, or service-local model.
2. Identify its owner and relationship in the aggregate graph.
3. Determine whether it is local-only, cloud-only, or shared across both
   deployment modes.
4. Add or update EF Core configuration, indexes, converters, and migrations
   in `Services.Data`.
5. Update every mapper, query projection, orchestrator, and UI surface that
   depends on the entity.
6. Consider redaction, encryption, retention, and ownership implications for
   sensitive data.
7. Preserve existing cascade and uniqueness semantics unless the change is
   intentional and documented.

Do not add external SDK types, `DbContext` dependencies, HTTP clients, file
access, or orchestration methods to these classes. If behavior requires
coordination, place it in the appropriate `Services` module and keep the
entity as the stable persisted contract.

## File Map

| File | Purpose |
|---|---|
| `BaseEntity.cs` | Shared ID and audit timestamps. |
| `User.cs` | Abstract common user identity and application ownership. |
| `LocalUser.cs` | Local email/password and verification state. |
| `RemoteUser.cs` | GitHub identity and Azure connection state. |
| `Application.cs` | User-owned local or remote source project. |
| `CsProject.cs` | C# project within an application. |
| `Configuration.cs` | Persisted project deployment settings. |
| `Deployment.cs` | Local/cloud deployment history and runtime identifiers. |

## Related Documentation

- [`Core`](../README.md)
- [`Core/DTO`](../DTO/README.md)
- [`Services`](../../Services/README.md)
- [`Web`](../../Web/README.md)
- [Solution navigation map](../../.agents/navigation.md)
