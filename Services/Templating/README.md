# Templating

`Services/Templating` generates deployment artifacts for local Docker and
cloud Azure Container Apps workflows. It uses a JSON manifest to select
Scriban templates, builds a stable anonymous model from Core DTOs, renders
matching files, and optionally writes them to a target directory.

The module owns artifact generation and output-path safety. It does not scan
projects, run Docker, call Azure or GitHub, persist deployment state, or
decide when a deployment should start.

## Responsibilities

- Load and cache `template-manifest.json`.
- Select active templates for local, cloud, or all deployment targets.
- Build the unified Scriban model from deployment configuration and project
  metadata.
- Normalize project, database, cloud-resource, and secret names used by
  generated files.
- Render Scriban templates asynchronously.
- Return generated `TemplateFile` values for remote GitHub commits.
- Write generated files beneath a local output directory.
- Reject template and output paths that escape their intended roots.
- Coordinate deterministic GitHub Actions secret names with cloud
  orchestration.

The module does not:

- discover `.csproj` files or infer database providers;
- validate user authentication or deployment ownership;
- execute generated Docker, Bicep, or workflow files;
- upload files to GitHub;
- provision Azure resources;
- encrypt repository secrets;
- persist configuration or deployment records.

## Module Structure

```text
Services/Templating/
├── ITemplatingService.cs
├── TemplatingService.cs
├── TemplateManifestCatalog.cs
├── TemplateRuleMatcher.cs
├── TemplateModelFactory.cs
├── ScribanTemplateRenderer.cs
├── TemplateFileWriter.cs
├── TemplatePaths.cs
├── TemplateNameNormalizer.cs
├── CloudDeploymentSecretNames.cs
└── Templates/
    ├── template-manifest.json
    ├── Dockerfile.scriban
    ├── docker-compose.scriban
    ├── dockerignore.scriban
    ├── azure-aca.bicep.scriban
    └── github-actions.yml.scriban
```

Public contracts are defined in `Core/DTO`:

- `DeploymentConfigDto`
- `ProjectMetadataDto`
- `DatabaseConfigDto`
- `TemplateManifestRuleDto`
- `TemplateFile`

## Public Contract

`ITemplatingService` exposes two related operations:

| Method | Purpose | Main consumer |
|---|---|---|
| `GenerateAllTemplatesAsync` | Render matching templates and return relative paths/content without writing files. | `CloudDeploymentOrchestrator` |
| `GenerateAndSaveAllTemplatesAsync` | Render matching templates and write them beneath the target directory. | `LocalDeploymentOrchestrator` |

Both methods require:

- a mutable `DeploymentConfigDto`;
- scanned `ProjectMetadataDto`;
- the main C# project name;
- an output directory used for path resolution and model construction;
- a cancellation token.

`TemplateFile.Path` is a relative output path. The GitHub adapter resolves it
against the repository when committing; the local writer resolves it against
the requested output directory.

## Generation Pipeline

```text
DeploymentConfigDto + ProjectMetadataDto
    -> TemplateManifestCatalog
    -> TemplateRuleMatcher
    -> TemplateModelFactory
    -> ScribanTemplateRenderer
    -> List<TemplateFile>
    -> TemplateFileWriter or GitHubService
```

`GenerateAndSaveAllTemplatesAsync` first performs the same in-memory
generation as `GenerateAllTemplatesAsync`, then passes the result to
`TemplateFileWriter`. There is no separate local rendering path, so local and
cloud generation share naming, metadata, and escaping rules.

## Manifest-Driven Selection

`Templates/template-manifest.json` currently contains these active rules:

| Template | Output path | Target |
|---|---|---|
| `Dockerfile.scriban` | `Dockerfile` | Local |
| `dockerignore.scriban` | `Dockerfile.dockerignore` | Local |
| `Dockerfile.scriban` | `.automate/Dockerfile` | Cloud |
| `dockerignore.scriban` | `.automate/Dockerfile.dockerignore` | Cloud |
| `docker-compose.scriban` | `docker-compose.yml` | Local |
| `azure-aca.bicep.scriban` | `infra/main.bicep` | Cloud |
| `github-actions.yml.scriban` | `.github/workflows/deploy.yml` | Cloud |

`TemplateRuleMatcher.ShouldRender`:

1. skips inactive rules;
2. treats a blank deployment target as `All`;
3. renders `All` for both workflows;
4. renders `Cloud` only when `config.IsCloudDeployment` is true;
5. renders `Local` only when `config.IsCloudDeployment` is false.

Matching is case-insensitive. The deployment target is a manifest routing
value, not a new enum; preserve the supported `All`, `Local`, and `Cloud`
semantics when adding rules.

### Manifest loading and caching

`TemplateManifestCatalog` loads the manifest from the copied output path:

```text
<application-base>/Templating/Templates/template-manifest.json
```

The catalog:

- loads lazily;
- caches the parsed rules process-wide;
- serializes first load with a `SemaphoreSlim`;
- throws `FileNotFoundException` when the manifest is absent;
- logs parse failures and returns an empty rule list;
- rethrows cancellation.

The manifest is not reloaded during the process lifetime. Restart the
application after changing it, and ensure the updated file is copied to
output.

## Unified Template Model

`TemplateModelFactory.Create` combines deployment configuration and scanner
metadata into the names consumed by all Scriban templates.

### Project and Docker metadata

The model includes:

| Field | Meaning |
|---|---|
| `app_name` | Original application/project display name. |
| `project_name` | Main C# project name. |
| `app_slug` | Normalized application/image name. |
| `project_slug` | Normalized project name. |
| `dotnet_version` | Scanner-derived target .NET version. |
| `projects` | All dependency-graph projects with solution-relative paths/folders. |
| `main_project_relative_path` | Relative main `.csproj` path, when found. |
| `main_project_folder` | Relative folder used by Docker build commands. |

`AllProjectPaths` is converted relative to the resolved template root and
uses forward slashes. The main project is located by a filename ending in
`<csProjectName>.csproj`; when it cannot be found, Dockerfile rendering uses
its remote-repository fallback discovery path.

### Local deployment fields

The model includes:

- `exposed_port`;
- `environment_name`;
- `requires_db`;
- ordered `databases`;
- ordered `custom_env_vars`.

The local Compose template uses these values to create the web service,
database services, connection strings, ports, and custom environment
variables.

### Cloud deployment fields

The model includes:

- `is_cloud_deployment`;
- `azure_location`;
- `resource_group_name`;
- `container_app_name`;
- derived Container Apps environment, Log Analytics, and managed identity
  names;
- `registry_server`;
- `image_name`.

Cloud names use explicit configuration when provided and deterministic
fallbacks otherwise. The Bicep and GitHub Actions templates consume the same
model, so changing a name field affects both infrastructure and workflow
artifacts.

## Database Model and Generated Values

Each configured database becomes an ordered template entry containing:

- canonical provider type;
- database name and connection-string name;
- login requirement;
- local container suffix;
- URL-encoded local credentials where needed;
- cloud resource suffix;
- Bicep parameter/value names;
- GitHub Actions secret names;
- Container App secret name;
- connection-string environment variable name.

`TemplateNameNormalizer.NormalizeDatabaseType` maps aliases to:

- `PostgreSQL`;
- `MySQL`;
- `SQLServer`;
- `MongoDB`;
- `Redis`.

`PostgreSQL`, `MySQL`, and `SQLServer` require generated username/password
parameters. MongoDB and Redis use provider-specific connection formats in the
templates.

`CloudDeploymentSecretNames` creates deterministic names:

```text
AUTOMATE_DB_<index>_USERNAME
AUTOMATE_DB_<index>_PASSWORD
AUTOMATE_ENV_<index>_<normalized-key>_<hash>
```

Custom environment secret names use an eight-character SHA-256 suffix to
avoid collisions after key normalization and are capped at GitHub's
repository-secret length limit. The same names must be used by
`CloudRepositorySecretBuilder`, the generated GitHub workflow, and the Bicep
template.

Template generation does not encrypt secret values. It places values or
secret references into generated artifacts according to the deployment
target; `Services.GitHub` encrypts values only when sending repository
secrets to GitHub.

## Custom Environment Variables

Custom variables are:

1. filtered to non-empty keys;
2. sorted case-insensitively by key;
3. assigned stable zero-based indexes;
4. exposed to templates with Bicep parameter, decoded-value, GitHub-secret,
   and Container App secret names.

Stable ordering is important because the index participates in generated
secret names. Adding, removing, or renaming a variable can change subsequent
secret identifiers.

The local Compose template writes values directly into the generated
environment section. The cloud templates pass values through GitHub
repository secrets and Bicep secure parameters. Callers must avoid logging
the generated model or generated content when it contains credentials.

## Template Details

### `Dockerfile.scriban`

The Dockerfile template:

- uses the scanner-derived .NET SDK/runtime version;
- copies known project descriptors first to improve restore caching;
- restores and publishes the resolved main project when available;
- falls back to finding the first `.csproj` for remote repositories;
- publishes without an app host;
- exposes the configured port;
- uses the project DLL as the entry point when the main project is known.

### `docker-compose.scriban`

The local Compose template:

- builds the web image from the repository root and `.automate/Dockerfile`;
- maps the configured host port to container port `8080`;
- sets `ASPNETCORE_ENVIRONMENT`;
- generates provider-specific connection strings;
- adds custom environment variables;
- creates database services and `depends_on` entries when configured.

The generated file is consumed by `Services.Docker`; this module does not
validate whether Docker images, credentials, or database options will work at
runtime.

### `dockerignore.scriban`

The generated ignore file excludes source-control metadata, IDE files, build
output, dependency folders, local environment files, and publish artifacts.
It limits the context sent to Docker and helps prevent accidental inclusion
of local secrets.

### `azure-aca.bicep.scriban`

The Bicep template generates resource-group-scoped Azure infrastructure for:

- Log Analytics;
- a Container Apps managed environment;
- supported PostgreSQL, MySQL, SQL Server, MongoDB/Cosmos, and Redis resources;
- database connection secrets;
- custom environment secrets;
- the public Container App and its registry configuration.

It uses secure parameters for registry credentials, database credentials, and
custom environment values. Azure deployment is performed by the generated
GitHub Actions workflow, not by `TemplatingService`.

### `github-actions.yml.scriban`

The workflow template:

- triggers on the deployment branch push and manual dispatch;
- grants contents read, packages write, and id-token write permissions;
- builds and pushes the generated image to GHCR;
- logs into Azure through OIDC;
- ensures the resource group exists;
- deploys `infra/main.bicep`;
- passes generated database and custom-environment secret references.

The exact OIDC trust values and managed identity are prepared by
`Services.Azure`; this template only consumes the generated repository
secrets.

## Path Safety

`TemplatePaths` protects both source templates and generated outputs:

- `ResolveTemplatePath` prevents manifest template files from escaping the
  copied templates directory;
- `NormalizeRelativeOutputPath` rejects empty, rooted, or parent-traversal
  output paths;
- `ResolveOutputPath` verifies the final path remains under the requested
  output root;
- `ResolveTemplateRoot` determines the source root used for relative project
  paths.

For local generation into a `.automate` directory, the template root is the
parent directory of `.automate`, allowing Docker build contexts and project
paths to be relative to the repository root. For cloud generation with `.`
as the output anchor, the current directory is used.

Do not bypass `TemplatePaths` with `Path.Combine` in new writers or template
loaders. Manifest output paths are configuration input and must remain
confined to the intended destination.

## File Writing

`TemplateFileWriter`:

1. creates the output directory when absent;
2. resolves each relative output path through `TemplatePaths`;
3. creates parent directories;
4. writes content asynchronously with cancellation;
5. logs the generated output path.

It writes files in the order returned by manifest generation. It does not
delete stale files from prior generations. If a template is removed from the
manifest, an old generated file may remain and must be cleaned up by the
deployment workflow or an explicit maintenance operation.

## Consumers and Boundaries

| Consumer | Templating responsibility |
|---|---|
| `Services.Scanner` | Supplies `ProjectMetadataDto`, package/dependency data, and inferred database configuration. |
| `Services.Orchestration.LocalDeploymentOrchestrator` | Generates and saves local Docker artifacts under `.automate`. |
| `Services.Orchestration.CloudDeploymentOrchestrator` | Generates cloud artifacts and passes returned files to GitHub. |
| `Services.GitHub` | Commits returned `TemplateFile` values to the deployment branch. |
| `Services.Docker` | Executes generated Dockerfile/Compose artifacts. |
| `Services.Azure` | Prepares the identity and resources consumed by generated cloud files. |
| `Web` | Edits `DeploymentConfigDto` before generation; it does not render templates. |

Keep provider calls and deployment sequencing outside this module. If a
template needs new data, add it to the model factory and trace the DTO,
scanner, orchestrator, generated output, and provider consumers together.

## Error, Cancellation, and Logging Rules

- Validate template and output paths before reading or writing files.
- Pass cancellation tokens through manifest loading, template reads, rendering,
  and file writes.
- Preserve `OperationCanceledException`.
- Treat a missing templates directory or manifest as an infrastructure
  failure.
- Treat a missing individual template file as a skipped generated file after
  logging a warning.
- Treat Scriban parse errors as generation failures.
- Do not convert an empty generated file list into a successful deployment;
  cloud orchestration explicitly rejects it.
- Never log complete models, generated secret values, connection strings, or
  custom environment values.
- Log paths and rule/template names only when they are safe for the hosting
  environment.

## Security Rules

- Keep output path traversal protection in place.
- Do not render untrusted template file paths outside the copied template
  root.
- Treat `DeploymentConfigDto.CustomEnvVars`, database passwords, and
  connection strings as sensitive.
- Avoid adding secrets directly to templates when a provider secret reference
  can be used.
- Do not commit generated local `.env` or credential files through this
  module.
- Keep repository-secret encryption in `Services.GitHub`.
- Review generated Docker, Bicep, and workflow output for command
  interpolation, public exposure, and credential leakage when changing the
  model.

Generated files are deployment artifacts and can contain sensitive values in
the local Compose case. The caller is responsible for protecting the output
directory and controlling whether generated files are committed or uploaded.

## Dependency Injection

`Web.Configs.ServiceConfiguration` registers:

```csharp
services.AddScoped<ITemplatingService, TemplatingService>();
```

`TemplatingService` receives `ILogger<TemplatingService>` and creates focused
helpers for manifest loading, rendering, and writing. The helpers are
stateless apart from process-wide manifest caching.

Templates are copied to application output by `Services/Services.csproj`:

```xml
<None Update="Templating\Templates\*">
    <CopyToOutputDirectory>PreserveNewest</CopyToOutputDirectory>
</None>
```

If a new template or manifest file is not copied, runtime generation will
fail even though the source file exists in the repository.

## Testing Guidance

Use temporary directories and representative DTOs to test:

- manifest loading, caching, missing files, malformed JSON, and cancellation;
- `All`, `Local`, `Cloud`, inactive, and blank-target rule matching;
- relative project-path and main-project resolution;
- database type aliases, login requirements, and deterministic secret names;
- stable custom environment-variable ordering;
- local Compose output and cloud Bicep/workflow output;
- missing individual templates and Scriban parse failures;
- absolute and parent-traversal template/output paths;
- output-directory creation and nested file writing;
- cancellation during reads, rendering, and writes.

Snapshot or golden-file tests are useful for generated Docker, Bicep, and
workflow artifacts. Do not place real credentials in fixtures or snapshots.

## Extending the Module

When adding a template or generated field:

1. Add or update the manifest rule and ensure the file is copied to output.
2. Decide whether the artifact is `Local`, `Cloud`, or `All`.
3. Add the required model field in `TemplateModelFactory`.
4. Define normalization, ordering, and fallback behavior.
5. Preserve relative output paths and path-root safety.
6. Update secret-name coordination in `CloudDeploymentSecretNames` and
   `CloudRepositorySecretBuilder` when credentials are involved.
7. Verify both local and cloud consumers of the generated output.
8. Add golden-file or focused rendering tests and update module docs.

Avoid putting conditional business workflow logic into Scriban when it belongs
in Scanner or Orchestration. Templates should format a prepared model; the
service should prepare deterministic, provider-appropriate values.

## File Map

| File | Purpose |
|---|---|
| `ITemplatingService.cs` | Public generation and write contract. |
| `TemplatingService.cs` | Manifest selection, model creation, rendering, and write coordination. |
| `TemplateManifestCatalog.cs` | Cached manifest loader. |
| `TemplateRuleMatcher.cs` | Local/cloud/active rule selection. |
| `TemplateModelFactory.cs` | Unified Scriban model and database/environment projections. |
| `ScribanTemplateRenderer.cs` | Safe template path resolution, parsing, and rendering. |
| `TemplateFileWriter.cs` | Traversal-safe asynchronous output writer. |
| `TemplatePaths.cs` | Template/output roots and path validation. |
| `TemplateNameNormalizer.cs` | Docker, Azure, database, and Container App secret names. |
| `CloudDeploymentSecretNames.cs` | Deterministic GitHub Actions secret names. |
| `Templates/template-manifest.json` | Active template-to-output/deployment mapping. |
| `Templates/Dockerfile.scriban` | Local/cloud .NET Dockerfile. |
| `Templates/docker-compose.scriban` | Local web/database Compose definition. |
| `Templates/dockerignore.scriban` | Generated Docker build-context exclusions. |
| `Templates/azure-aca.bicep.scriban` | Azure Container Apps and database infrastructure. |
| `Templates/github-actions.yml.scriban` | GitHub Actions build, push, OIDC, and Bicep deployment workflow. |

## Related Documentation

- [`Services`](../README.md)
- [`Services/Scanner`](../Scanner/README.md)
- [`Services/Orchestration`](../Orchestration/README.md)
- [`Services/Docker`](../Docker/README.md)
- [`Services/GitHub`](../GitHub/README.md)
- `Services/Azure` when its module README is added
- [`Core/DTO`](../../Core/DTO/README.md)
- [`Web`](../../Web/README.md)
- [Solution navigation map](../../.agents/navigation.md)
