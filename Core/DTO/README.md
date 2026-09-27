# Core DTOs

`Core/DTO` contains the framework-neutral data-transfer contracts shared by
`Services` and `Web`. These types describe information moving between scanners,
the UI, deployment orchestration, template generation, and external GitHub or
Azure integrations.

This module should remain a contract-only layer. DTOs may model data and
provide safe defaults, but they should not perform I/O, validation workflows,
serialization orchestration, persistence, deployment, or UI behavior.

## Responsibilities

- Represent local repository and C# project discovery results.
- Carry parsed `.csproj` metadata and dependency-graph information.
- Represent deployment configuration supplied by the UI and consumed by
  local/cloud workflows.
- Describe database-provider and template-manifest rules loaded from JSON.
- Carry generated template files between the templating and GitHub/local-file
  integrations.
- Project only the GitHub and Azure fields required by AutoMate.

## Contract Families

### Project discovery and analysis

| Type | Role | Main producer/consumer |
|---|---|---|
| `LocalProjectDto` | A discovered local repository or solution folder, including its C# projects. | `Services.Scanner` produces it; `Web` displays it. |
| `CsProjectDto` | A discovered `.csproj` name, path, and web-project flag. | `Services.Scanner` produces it; `Services.Data` and `Web` consume it. |
| `ProjectMetadataDto` | Parsed target framework, project references, package references, and dependency-graph paths. | `Services.Scanner` produces it; `Services.Templating` and cloud orchestration consume it. |
| `DbProviderRuleDto` | A package-fragment rule identifying a database provider. | `Services.Scanner` loads and applies it. |

The discovery flow is:

```text
LocalSystemScannerService
    -> LocalProjectDto
        -> CsProjectDto
            -> ApplicationService

ProjectScannerService
    -> ProjectMetadataDto
    -> DbProviderRuleDto matches
    -> DeploymentConfigDto.Databases
```

`ProjectMetadataDto.ProjectReferences` contains referenced project paths as
reported for the project. `AllProjectPaths` is the flattened, absolute-path
set for the complete dependency graph. Package dictionaries use
case-insensitive key comparers so package-name matching is not sensitive to
NuGet casing.

### Deployment configuration

| Type | Role |
|---|---|
| `DeploymentConfigDto` | Mutable configuration edited by Blazor forms and passed to local or cloud deployment workflows. |
| `DatabaseConfigDto` | One database definition rendered into local/cloud deployment assets. |
| `CloudDeploymentRequestDto` | Immutable cloud-deployment input combining configuration, metadata, repository identity, and credentials. |

`DeploymentConfigDto` is intentionally a class with `get; set;` properties.
The `Web` configuration form updates it while the user edits deployment
settings. Most other DTOs are records with `init` properties because they are
snapshots or messages that should be assembled once and then treated as
stable.

Deployment configuration contains two kinds of values:

- **Local/general values:** `ProjectId`, `CsProjectId`, `ProjectName`,
  `EnvironmentName`, `ExposedPort`, `Databases`, and `CustomEnvVars`.
- **Cloud values:** `CloudAzureRegion`, `CloudResourceGroupName`,
  `CloudContainerAppName`, and `CloudRegistryName`.

`IsCloudDeployment` selects the target workflow. Callers should not infer the
target from whether cloud fields happen to be populated.

`DatabaseConfigDto` defaults are template/development defaults from
`Core.Defaults.DeploymentDefaults`. `DbPassword` is not an application secret;
real credentials must be supplied through the repository’s secret,
environment-variable, or provider-secret mechanisms.

### Templates

| Type | Role |
|---|---|
| `TemplateManifestRuleDto` | Describes which template file maps to which output file and deployment target. |
| `TemplateFile` | Contains rendered file content and its relative output path. |

The manifest flow is:

```text
Templating/Templates/template-manifest.json
    -> TemplateManifestRuleDto
    -> template matching and Scriban rendering
    -> TemplateFile
    -> local filesystem or GitHub branch
```

`TemplateManifestRuleDto.DeploymentTarget` currently uses the values `All`,
`Local`, or `Cloud`. `IsActive` allows a rule to remain in the manifest while
temporarily disabling rendering. `TemplateFile.Path` is a relative output
path; writers and GitHub clients are responsible for resolving it against
their destination.

### External integration projections

| Type | Role |
|---|---|
| `GitHubRepositoryDto` | Minimal GitHub repository projection used for repository import and caching. |
| `GitHubWorkflowRunDto` | Minimal GitHub Actions run projection used for polling and UI/deployment tracking. |
| `AzureCloudCredentialsDto` | Azure tenant, subscription, and delegated access token needed to prepare cloud resources. |
| `AzureOidcSetupResultDto` | Result of configuring the managed identity and GitHub Actions OIDC trust. |

These records deliberately do not mirror complete provider SDK models. Add a
property only when AutoMate needs it. `GitHubRepositoryDto` uses
`JsonPropertyName` attributes because its property names follow the C# naming
convention while GitHub's JSON uses names such as `full_name` and
`updated_at`.

## File Map

| File | Purpose |
|---|---|
| `LocalProjectDto.cs` | Local repository/solution discovery result. |
| `CsProjectDto.cs` | Discovered C# project descriptor. |
| `ProjectMetadataDto.cs` | Parsed project and dependency metadata. |
| `DbProviderRuleDto.cs` | Database package-detection rule. |
| `DeploymentConfigDto.cs` | User-editable deployment configuration. |
| `DatabaseConfigDto.cs` | Database template configuration. |
| `CloudDeploymentRequestDto.cs` | Complete cloud deployment request. |
| `TemplateManifestRuleDto.cs` | Template selection and output rule. |
| `TemplateFile.cs` | Rendered artifact path/content pair. |
| `GitHubRepositoryDto.cs` | GitHub repository API projection. |
| `GitHubWorkflowRunDto.cs` | GitHub Actions run projection. |
| `AzureCloudCredentialsDto.cs` | Azure deployment credentials. |
| `AzureOidcSetupResultDto.cs` | Azure/GitHub OIDC setup result. |

## Public Usage Boundaries

- `Services/Scanner` should create
  discovery, metadata, and provider-rule DTOs.
- `Services/Templating` should consume
  deployment and metadata DTOs and return `TemplateFile` values.
- `Services/Orchestration` should
  coordinate `DeploymentConfigDto` and `CloudDeploymentRequestDto`; workflow
  rules do not belong in these contracts.
- `Services/GitHub` and `Services/Azure` should map provider
  responses and credentials into the external-integration DTOs.
- `Web` may bind forms and display DTO data, but should
  not add infrastructure behavior to these types.

## Conventions

- Keep all types in the `Core.DTO` namespace.
- Prefer records with `init` properties for immutable snapshots and messages.
- Preserve `DeploymentConfigDto` mutability because the Blazor form edits it.
- Initialize strings and collections to non-null defaults; nullable values
  should express a meaningful absence, such as
  `ProjectMetadataDto.UserSecretsId` or workflow `Conclusion`.
- Use `StringComparer.OrdinalIgnoreCase` for dictionaries whose keys originate
  from package names or project metadata.
- Use `Core.Defaults.DeploymentDefaults` instead of duplicating shared
  deployment defaults.
- Keep DTOs free from framework, SDK, database, filesystem, and UI
  dependencies.
- Do not add domain behavior, service calls, logging, or validation side
  effects to DTOs.

## Sensitive Data

`AzureCloudCredentialsDto.AccessToken`,
`CloudDeploymentRequestDto.GitHubAccessToken`, and
`CloudDeploymentRequestDto.GitHubContainerRegistryToken` carry credentials
between trusted application services. They are contracts, not storage or
security boundaries:

- Do not log DTO instances or serialize them into user-facing responses.
- Do not persist these values through `Core`.
- Keep encryption, secret storage, redaction, and provider-specific handling
  in `Services`.
- Avoid copying credentials into DTOs unless the receiving workflow requires
  them.

## Extending the DTO Module

Before adding a property or a new DTO:

1. Confirm that the data crosses a module boundary and cannot remain an
   implementation detail.
2. Identify the producer, every consumer, and whether the value is persisted,
   rendered, sent to a provider, or displayed.
3. Choose an immutable record for a snapshot/message or a mutable class only
   when an existing editing workflow requires it.
4. Use an existing enum or shared default when the value has solution-wide
   semantics; do not introduce repeated magic strings.
5. Update the producing and consuming module documentation when the contract
   changes.

For provider payloads, prefer a narrow projection over importing a complete
SDK model. For template and deployment changes, update the corresponding
manifest, defaults, renderer, validator, and orchestration code rather than
placing conditional behavior in the DTO.

## Related Documentation

- [`Core`](../README.md)
- [`Services`](../../Services/README.md)
- [`Web`](../../Web/README.md)
- [Solution navigation map](../../.agents/navigation.md)
