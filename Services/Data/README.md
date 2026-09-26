# Data

`Services/Data` is AutoMate's Entity Framework Core persistence module. It
owns the database context, relational mapping, protected token conversion, and
the application-facing services used to read and write users and saved
applications.

The module persists the entities defined in `Core/Entities`; it does not
redefine domain contracts or own authentication, deployment, Docker, GitHub,
Azure, or UI workflows. Those modules call the interfaces in this directory
or use `AutoMateDbContext` when their workflow must coordinate several
entities.

## Responsibilities

- Configure `AutoMateDbContext` and its PostgreSQL model.
- Persist users, applications, C# projects, configurations, and deployments.
- Map the `User`/`LocalUser`/`RemoteUser` hierarchy using a discriminator.
- Enforce required fields, uniqueness, relationship ownership, and cascade
  deletion.
- Encrypt GitHub and Azure OAuth token values before they are written to the
  database.
- Persist ASP.NET Core Data Protection keys for use across application
  instances and container restarts.
- Maintain `BaseEntity` audit timestamps during `SaveChanges` operations.
- Provide user identity and credential projections through `IUserService`.
- Create, query, and delete saved local or GitHub applications through
  `IApplicationService`.

## Module Structure

```text
Services/Data/
├── AutoMateDbContext.cs
├── Apps/
│   ├── IApplicationService.cs
│   └── ApplicationService.cs
└── Users/
    ├── IUserService.cs
    └── UserService.cs
```

Database migrations are kept beside this module at
`Services/Migrations/`. They are generated schema history, not service
implementations, and must remain synchronized with `AutoMateDbContext`.

## Persistence Model

The context exposes these sets:

| DbSet | Stored responsibility |
|---|---|
| `Users` | Local email/password users and GitHub-backed remote users. |
| `Applications` | User-owned local folders or remote repository records. |
| `CsProjects` | Individual `.csproj` records within an application. |
| `AppConfigs` | One persisted deployment configuration per C# project. |
| `Deployments` | Local and cloud deployment history. |
| `DataProtectionKeys` | Shared ASP.NET Core Data Protection key material. |

The entity graph is owned through the following relationships:

```text
User
└── Application
    └── CsProject
        ├── Configuration  (one-to-one)
        └── Deployment     (one-to-many)
```

`User -> Application`, `Application -> CsProject`,
`CsProject -> Configuration`, and `CsProject -> Deployment` all use cascade
delete. Deleting a parent therefore removes the persisted child graph. Code
that deletes an application must first verify the requesting user owns it;
`ApplicationService` performs this check in `DeleteAppAsync` and
`GetAppByIdAsync`.

## `AutoMateDbContext`

### Registration and database provider

`Web/Configs/ServiceConfiguration.cs` registers the context with
`AddDbContextPool` and configures PostgreSQL through `UseNpgsql`. Snake-case
naming is enabled, so CLR properties such as `CreatedAt` map to columns such
as `created_at`.

The context is also registered as the storage provider for ASP.NET Core Data
Protection:

```csharp
services.AddDataProtection()
    .PersistKeysToDbContext<AutoMateDbContext>()
    .SetApplicationName(AppName);
```

This makes authentication and token-protection keys durable across process
restarts and shareable by multiple application instances. The database must
therefore be available before protected cookie or token operations can work
reliably.

### User hierarchy

`User` uses table-per-type hierarchy mapping in the `users` table:

| CLR type | `user_type` value |
|---|---|
| `LocalUser` | `local` |
| `RemoteUser` | `github` |

Email is required and unique across both user types. The context also limits
usernames to 100 characters and email addresses to 255 characters.

### Relationship and field constraints

The context configures:

- required application names with a maximum length of 200;
- Azure account, tenant, and subscription identifiers with a maximum length
  of 100;
- one configuration per `CsProject` through a unique foreign-key index;
- cascade deletion through the aggregate graph;
- PostgreSQL-compatible UUID and UTC timestamp persistence.

The enum properties on `Application` and `Deployment` are stored as integer
values. Their numeric compatibility rules are documented in
`Core/Enums/README.md`; do not reorder existing enum members casually.

### Audit timestamps

All synchronous and asynchronous `SaveChanges` overloads call
`UpdateAuditFields` before EF writes changes:

- added `BaseEntity` instances receive both `CreatedAt` and `UpdatedAt`;
- modified instances receive a refreshed `UpdatedAt`;
- unchanged and deleted instances are not updated.

The timestamps use `DateTimeOffset.UtcNow`. Callers should not depend on
these values being final until persistence succeeds.

## Protected Token Persistence

`AutoMateDbContext` installs EF Core value converters for:

- `RemoteUser.GitHubAccessToken`;
- `RemoteUser.AzureAccessToken`;
- `RemoteUser.AzureRefreshToken`.

Each token family uses a dedicated Data Protection purpose string. A non-null
value is protected before persistence and unprotected when materialized;
null remains null. The entity classes contain the logical values, while this
module owns the storage encryption boundary.

Rules for token-bearing data:

- Never log a full `User` or `RemoteUser` entity.
- Prefer projections that select only the credential required by the caller.
- Do not expose OAuth tokens through UI models or general application
  responses.
- Keep Data Protection key persistence stable when deploying multiple
  instances; changing the application name or key store can make existing
  cookies and protected values unreadable.
- Treat a failed unprotect operation as a real configuration or key-material
  problem; do not replace it with an empty-token success path.

## `Users` Services

`IUserService` is the narrow read boundary for identity resolution and cloud
credentials. `UserService` uses `AsNoTracking` for these lookups and projects
only the required values.

### Identity lookup

`GetUserDetailsFromIdentifierAsync` accepts either:

- AutoMate's internal user GUID, or
- a GitHub account ID string.

For a remote user it returns the internal ID, the protected-and-decrypted
GitHub access token, and `IsGitHubUser = true`. For a local user it returns
the internal ID with no GitHub token and `IsGitHubUser = false`.

Invalid, empty, or unknown identifiers return
`(Guid.Empty, null, false)`. This method is used by the Web authenticated-user
resolver and by GitHub-related UI flows.

`GetUserIdByGithubAccountIdAsync` is the focused remote-user lookup used when
the current principal contains a GitHub account identifier.

### Azure connection and credentials

`HasAzureConnectionAsync` checks that a remote user has an Azure account ID,
tenant ID, and access token. It is intended for connection-state checks, not
for returning credentials.

`GetAzureCloudCredentialsAsync` requires a valid user ID, tenant ID,
subscription ID, and access token, then returns an `AzureCloudCredentialsDto`.
The refresh token is not included in this DTO because deployment preparation
needs the delegated access token and subscription context, not the full
stored credential record.

Callers should pass cancellation tokens and treat null or false results as
"not connected/not found", not as permission to invent credentials.

## `Apps` Services

`IApplicationService` is the application-facing persistence boundary for
saved source projects.

### Adding local applications

`AddLocalAppAsync` accepts discovery DTOs and:

1. validates the user ID;
2. trims the project and source paths/names;
3. finds an existing local application for the same user and source path;
4. creates the application if necessary;
5. rejects a duplicate C# project with the same exact project-file path;
6. creates a new `CsProject` with default configuration when needed;
7. saves the aggregate.

New local projects receive these defaults:

| Setting | Default |
|---|---|
| `DotNetVersion` | `10.0` |
| `LocalExposedPort` | `8080` |
| `RequiresDb` | `false` |
| `IsPublic` | `false` |

The service returns `false` for invalid input, duplicates, or
`DbUpdateException` failures and logs the reason. It does not throw a
user-facing exception for an expected duplicate.

### Adding GitHub applications

`AddGitHubAppAsync` trims the repository URL, uses the URL as a fallback name
when the supplied name is blank, and rejects an existing remote application
for the same user and exact source URL. Remote applications are initially
stored without a C# project; cloud orchestration can later resolve an existing
web project or create a default one.

### Reading and deleting applications

`GetUserAppsAsync` and `GetAppByIdAsync` use a shared no-tracking query that
eagerly loads:

```text
Application
└── CsProjects
    └── Deployments
```

The query uses `AsSingleQuery`, so callers receive a complete graph from one
EF query rather than relying on lazy loading. `GetAppByIdAsync` and
`DeleteAppAsync` include the owner ID in their predicate to prevent
cross-user access.

## Consumers and Boundaries

| Consumer | Data responsibility |
|---|---|
| `Services.Auth` | Creates and updates users, verifies local accounts, and persists OAuth profile data. |
| `Services.Orchestration` | Creates projects/configurations and deployment records; updates deployment status. |
| `Web` authenticated-user resolver | Resolves internal IDs from local or GitHub identifiers. |
| `Web` dashboard and repository pages | Lists, adds, and deletes user-owned applications. |
| `Web` project details | Loads application/deployment graphs and Azure credentials. |
| `Web.Hubs.LogHub` | Loads the authorized project graph before streaming logs. |
| `Web` startup | Checks database connectivity and configures the context/key store. |

Keep HTTP, Blazor, Docker, GitHub, Azure SDK, and SignalR concerns outside
this module. The data services may accept DTOs and return entities where the
existing contract requires them, but they should not start coordinating
external workflows.

## Query and Mutation Conventions

- Use `AsNoTracking` for read-only lookups and UI read models.
- Project only the fields required for identity or credential operations.
- Include navigation properties explicitly when a caller needs a graph.
- Include the owning user ID in application reads and deletes.
- Pass `CancellationToken` through every asynchronous EF operation.
- Normalize and validate external input before constructing entities.
- Save a coherent aggregate once its required relationships are assembled.
- Catch `DbUpdateException` only where the public service contract defines a
  failure result; do not silently swallow unexpected exceptions.
- Use the context directly in orchestration when multiple entity types must
  participate in one workflow or transaction.

## Migrations and Schema Changes

Migrations live in `Services/Migrations`, while the startup project is `Web`.
Run EF commands from the `Web` directory:

```powershell
dotnet ef migrations add <MigrationName> --project ..\Services
dotnet ef database update --project ..\Services
```

When changing persisted state:

1. Update the relevant entity and `AutoMateDbContext` configuration together.
2. Consider indexes, nullability, cascade behavior, enum numeric values, and
   token protection requirements.
3. Generate a migration from `Web` targeting `Services`.
4. Review the generated `Up` and `Down` operations.
5. Apply the migration against a representative PostgreSQL database.
6. Update affected query projections, DTO mappings, orchestrators, and UI
   consumers.

Do not edit generated migration files or the model snapshot as a substitute
for changing the model and generating a migration. Existing production data
and protected token values must remain readable throughout a migration.

## Extending the Module

Before adding a new data service or persisted field:

1. Confirm the value belongs to durable domain state rather than a DTO,
   options object, or transient workflow model.
2. Identify its aggregate owner and authorization boundary.
3. Decide whether the value is local-only, remote-only, or shared.
4. Add explicit EF mapping, constraints, indexes, and converters where
   required.
5. Define a narrow interface when callers need a reusable application-facing
   operation.
6. Use projections for secrets and identity lookups.
7. Add or update a migration and review destructive behavior.
8. Trace every consumer before changing an existing return type or default.

Do not add generic repository abstractions merely to wrap `DbSet`. Existing
services use focused queries and aggregate-specific operations; follow that
pattern unless a concrete cross-module need justifies a different boundary.

## File Map

| File | Purpose |
|---|---|
| `AutoMateDbContext.cs` | EF Core sets, relationships, constraints, token converters, and audit timestamps. |
| `Apps/IApplicationService.cs` | Public application persistence contract. |
| `Apps/ApplicationService.cs` | Local/GitHub application creation, loading, ownership checks, and deletion. |
| `Users/IUserService.cs` | Public identity and Azure credential lookup contract. |
| `Users/UserService.cs` | No-tracking user projections and credential availability checks. |
| `../Migrations/` | Generated PostgreSQL schema history for the context. |

## Related Documentation

- [`Services`](../README.md)
- [`Core/Entities`](../../Core/Entities/README.md)
- [`Core/DTO`](../../Core/DTO/README.md)
- [`Core/Enums`](../../Core/Enums/README.md)
- [`Web`](../../Web/README.md)
- [Solution navigation map](../../.agents/navigation.md)
