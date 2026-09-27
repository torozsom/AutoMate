# Core Enums

`Core/Enums` contains the small set of shared enumerations used to classify
project sources, application types, and deployment lifecycle state. These
values are part of the domain contract consumed by persisted entities,
orchestration services, and Blazor UI components.

Enums in this module should remain framework-neutral. They must not contain
workflow logic, persistence code, provider-specific values, or UI formatting.

## Enum Map

| Enum | Used by | Meaning |
|---|---|---|
| `SourceType` | `Application.SourceType`, application services, dashboard/project pages | Whether source code is local or hosted in a remote repository. |
| `AppType` | `Application.AppType` | The supported application style/classification. |
| `DeploymentStatus` | `Deployment.Status`, orchestration, cleanup, notifications, UI | The persisted and published state of a deployment attempt. |

## `SourceType`

`SourceType` identifies where an `Application` obtains its source code:

| Value | Meaning | Current behavior |
|---|---|---|
| `Local` | Source is on the local filesystem. | The UI scans the local project, analyzes dependencies, generates local artifacts, and queues Docker Compose deployment. |
| `Remote` | Source is hosted in a remote repository, currently GitHub. | The UI requires an Azure connection and queues cloud deployment preparation through GitHub and Azure. |

`Services.Data.Apps.ApplicationService` uses this enum when:

- detecting duplicate local applications by source path;
- detecting duplicate remote applications by source URL;
- creating local or remote `Application` entities.

`Web.Components.Pages.Dashboard` and `ProjectDetails` branch on `SourceType`
to choose local versus cloud deployment behavior. A remote application is not
necessarily limited to GitHub at the enum level, but the current integration
supports GitHub repositories.

When adding another source provider, keep `SourceType` focused on the
local/remote distinction. Provider identity, repository metadata, and
provider-specific credentials belong in the appropriate integration model or
entity fields rather than in this enum.

## `AppType`

`AppType` classifies an `Application`:

| Value | Meaning |
|---|---|
| `WebApi` | ASP.NET Core Web API application. |
| `Blazor` | Blazor application. |
| `Mvc` | ASP.NET Core MVC application. |

The value is persisted through `Application.AppType`. It is currently a
classification field rather than the primary deployment selector: deployment
eligibility is generally determined from `CsProject.IsWebProject` and the
selected deployment configuration.

Because `WebApi` is the first enum member and no explicit value is assigned,
the default value of a newly constructed `Application` is `WebApi`. New
application creation should set `AppType` explicitly when the scanner or
import workflow knows the actual application style.

If a new application style is added:

- update all classification logic and display labels;
- decide whether scanner detection or project metadata must change;
- preserve compatibility with existing persisted values;
- do not use `AppType` as a substitute for `IsWebProject` unless the workflow
  explicitly requires that change.

## `DeploymentStatus`

`DeploymentStatus` represents the state of one persisted `Deployment` record:

| Value | Meaning | Typical source |
|---|---|---|
| `Starting` | Deployment has been created or preparation has begun, but it is not yet operational. | Local/cloud orchestrator when a deployment record is created or Docker work starts. |
| `Running` | Deployment is considered active or the cloud workflow has reached the active tracking phase. | Successful local Docker startup or cloud deployment preparation/workflow tracking. |
| `Stopped` | A previously active deployment is no longer running. | Local stop operation or startup reconciliation against Docker state. |
| `Failed` | Deployment preparation or execution did not complete successfully. | Orchestrator exceptions, queued-job failures, or cleanup of stale `Starting` records. |

The current lifecycle is operational rather than a strict state machine:

```text
Starting -> Running
Starting -> Failed
Running  -> Stopped
Running  -> Failed
Stopped  -> Running   (startup reconciliation when Docker is active again)
```

Other transitions may occur during failure handling or reconciliation. The
enum does not enforce valid transitions; `Services.Orchestration` owns status
updates and notifications.

### Status persistence

`DeploymentStatus` is stored as an integer in PostgreSQL because EF Core's
default enum conversion is used. The current numeric values are:

| Value | Numeric representation |
|---|---:|
| `Starting` | `0` |
| `Running` | `1` |
| `Stopped` | `2` |
| `Failed` | `3` |

Do not reorder or remove existing members. Reordering would reinterpret
existing database rows. If a status must be retired, leave its numeric
position intact and migrate data deliberately. If long-term storage
compatibility or external serialization becomes important, consider explicit
numeric values and a migration strategy before adding more members.

### Status consumers

- `DeploymentStatusUpdater` persists a new status and publishes it through
  `IDeploymentStatusNotifier`.
- `DeploymentStatusNotifier` sends status changes to subscribed Blazor
  components.
- `DeploymentJobWorker` publishes `Failed` when a queued deployment fails
  before the orchestrator can update its record.
- `DeploymentCleanupHostedService` marks stale `Starting` records as `Failed`
  and synchronizes `Running`/`Stopped` with the Docker daemon.
- `Dashboard` and `ProjectDetails` use `Starting` to disable or show active
  deployment actions and use terminal states to refresh UI state.

Status values should be treated as persisted operational facts, not as
user-facing text. UI labels, badges, colors, and localized wording belong in
`Web`.

## Defaults and C# Semantics

These enums use implicit underlying `int` values and do not define an
`Unknown` member:

- `default(SourceType)` is `Local`;
- `default(AppType)` is `WebApi`;
- `default(DeploymentStatus)` is `Starting`.

This is convenient for the current model but means an uninitialized value can
look valid. Entity creation and deserialization paths should assign values
intentionally when the source or lifecycle state is known. Avoid treating
`default` as proof that a value was supplied by a caller.

## Boundaries

- Entities may persist these enum values.
- `Services` may make workflow decisions from them.
- `Web` may render them and select UI behavior.
- DTOs may carry them when a contract needs the domain classification.
- The enum definitions must not depend on EF Core, ASP.NET, Docker, GitHub,
  Azure, SignalR, or Blazor.

Use `SourceType` for source-location decisions, `AppType` for application
classification, and `DeploymentStatus` for deployment lifecycle. Do not add
unrelated concerns to an existing enum simply because the values appear
convenient.

## Extending the Enums

Before changing an enum:

1. Search all source, persistence mappings, queries, switch expressions,
   filters, UI conditions, and documentation that use it.
2. Determine whether the change affects stored integer values, serialized
   values, or external API contracts.
3. Add a new member at the end unless explicit values and a migration plan
   make another position safe.
4. Update all exhaustive switches and user-facing mappings.
5. Add or update tests for the new behavior and migration compatibility.
6. Update the relevant entity, service, UI, and module documentation.

Do not replace an enum with a string merely to avoid updating consumers.
Strings are appropriate for provider-reported values such as GitHub workflow
`Status` and `Conclusion`; these shared domain concepts use enums so
application code can make consistent decisions.

## File Map

| File | Purpose |
|---|---|
| `SourceType.cs` | Local versus remote application source. |
| `AppType.cs` | Supported application style classification. |
| `DeploymentStatus.cs` | Persisted deployment lifecycle state. |

## Related Documentation

- [`Core`](../README.md)
- [`Core/Entities`](../Entities/README.md)
- [`Core/DTO`](../DTO/README.md)
- [`Services`](../../Services/README.md)
- [`Web`](../../Web/README.md)
- [Solution navigation map](../../.agents/navigation.md)
