# Scanner

`Services/Scanner` discovers local .NET repositories and analyzes individual
projects for deployment preparation. It converts filesystem, `.csproj`, JSON,
`.env`, and package metadata into framework-neutral DTOs consumed by Web,
Data, Templating, and Orchestration.

The module is read-only with respect to the scanned source tree. It does not
persist applications, generate deployment files, run Docker, or decide the
final deployment workflow.

## Responsibilities

- Recursively discover Git repositories under a selected local root.
- Ignore build, dependency, IDE, hidden, and reparse-point directories.
- Detect `.sln`, `.slnx`, and `.csproj` files.
- Identify ASP.NET Core web projects from the SDK attribute.
- Parse target frameworks, package references, project references, and
  User Secrets identifiers.
- Traverse referenced projects and combine package metadata.
- Detect database providers from `database-providers.json`.
- Create baseline mutable deployment configuration DTOs.
- Allocate an available local host port for deployment configuration.
- Extract environment variables from `appsettings*.json`, `launchSettings.json`,
  and `.env` files.
- Flatten nested configuration keys into .NET environment-variable notation.

The module does not:

- save discovered projects to `Services.Data`;
- validate or persist user-entered deployment settings;
- read package assets from NuGet;
- evaluate MSBuild conditions or imported project files;
- execute the project or inspect runtime behavior;
- redact or encrypt extracted environment values.

## Module Structure

```text
Services/Scanner/
├── ILocalSystemScannerService.cs
├── LocalSystemScannerService.cs
├── IProjectScannerService.cs
├── ProjectScannerService.cs
├── LocalScannerDirectoryRules.cs
├── LocalCsProjectParser.cs
├── CsprojMetadataReader.cs
├── ProjectDependencyGraphScanner.cs
├── DatabaseProviderRuleCatalog.cs
├── database-providers.json
├── ProjectEnvironmentVariableExtractor.cs
├── JsonConfigurationFlattener.cs
├── DotEnvLineParser.cs
└── ScannerPortProvider.cs
```

The public results are defined in `Core/DTO`:

- `LocalProjectDto`
- `CsProjectDto`
- `ProjectMetadataDto`
- `DbProviderRuleDto`
- `DeploymentConfigDto`
- `DatabaseConfigDto`

## Public Contracts

### `ILocalSystemScannerService`

| Method | Purpose | Result/failure behavior |
|---|---|---|
| `ScanForProjectsAsync` | Discover Git repositories and contained C# projects. | Empty list for invalid root; cancellation propagates. |
| `FindSolutionRootAsync` | Walk upward from a project file to find a Git/solution root. | Throws when the project file is missing; falls back to its directory when no marker exists. |

### `IProjectScannerService`

| Method | Purpose | Result/failure behavior |
|---|---|---|
| `ScanProjectContentAsync` | Parse one `.csproj` and its referenced project graph. | Throws when the root project file is missing or parsing fails. |
| `AnalyzeDependenciesAsync` | Build deployment defaults and infer database configuration. | Returns baseline config when provider analysis fails; cancellation propagates. |
| `ExtractEnvironmentVariablesAsync` | Merge supported configuration sources. | Returns collected values; non-cancellation extraction failures are logged and return the values collected so far. |

All asynchronous methods accept cancellation tokens. Filesystem traversal and
parsing should remain cancellable as the project size can be substantial.

## Local Repository Discovery

`LocalSystemScannerService.ScanForProjectsAsync`:

1. validates that the root path is non-empty and exists;
2. converts it to a full path;
3. recursively walks directories;
4. skips excluded directories;
5. treats a directory containing `.git` as a repository boundary;
6. scans that repository for `.csproj` files;
7. checks for a top-level `.sln` or `.slnx`;
8. parses each discovered project into `CsProjectDto`;
9. returns one `LocalProjectDto` per Git repository.

Once a Git directory is found, traversal processes that repository and does
not recursively discover nested Git repositories underneath it. The repository
name is the directory name, and the returned path is the full repository path.

`IsDotNetProject` is true when the repository has a top-level `.sln`/`.slnx`
or at least one `.csproj`. A repository may still be returned when it is not a
.NET project, with an empty project list.

### Excluded directories

`LocalScannerDirectoryRules` skips:

- `.git` during general traversal;
- `bin`;
- `obj`;
- `node_modules`;
- `testresults`;
- `.vs`;
- `.idea`;
- any other hidden directory except `.git`;
- reparse-point directories.

The `.git` directory is skipped as content, but its presence is used to
identify a repository. These rules are centralized so discovery and nested
`.csproj` search remain consistent.

### Access and scan errors

Unauthorized directories are skipped with debug logging. Unexpected
directory/repository errors are logged and scanning continues where possible.
Cancellation is logged at debug level and rethrown. Do not change a
cancellation into a partial successful result without changing the public
contract deliberately.

## Solution Root Resolution

`FindSolutionRootAsync` starts at the directory containing a `.csproj` and
walks toward the filesystem root. The first directory containing one of these
markers is selected:

- `.git`;
- a `.sln` file;
- a `.slnx` file.

If no marker is found, the project directory is returned as a fallback. This
root is used by local orchestration to locate or create the `.automate`
deployment directory.

The method requires an existing project file and throws
`FileNotFoundException` otherwise. Callers should validate or surface this
failure rather than assuming the fallback applies to a missing path.

## `.csproj` and Dependency Graph Analysis

### Local discovery parser

`LocalCsProjectParser` reads the project XML only to determine whether the
root SDK contains `Microsoft.NET.Sdk.Web`. It creates a `CsProjectDto` with:

- filename without `.csproj` as the name;
- the full project file path;
- the detected web-project flag.

If parsing fails, the project is still returned with `IsWebProject = false`
and a warning. This keeps repository discovery resilient while allowing later
analysis to report a more specific failure.

### Metadata reader

`CsprojMetadataReader` reads:

- the first `TargetFramework`;
- or the first entry in `TargetFrameworks`;
- the target `.NET` version derived from a `net*` TFM;
- whether the root SDK is `Microsoft.NET.Sdk.Web`;
- `UserSecretsId`;
- package references and versions;
- project-reference paths.

Missing target frameworks use `net10.0` and `10.0` defaults. Package
dictionaries use `StringComparer.OrdinalIgnoreCase`. Project-reference
separators are normalized from backslashes to forward slashes.

The reader parses XML files directly. It does not evaluate MSBuild
properties, imported files, conditional item groups, central package
management, or generated assets. Treat the result as deployment-oriented
metadata rather than a complete MSBuild model.

### Dependency graph traversal

`ProjectDependencyGraphScanner` performs breadth-first traversal from the
root project:

1. read root metadata;
2. resolve each relative `ProjectReference` against the current project
   directory;
3. skip already visited or missing referenced files;
4. read each referenced project once;
5. collect referenced project package references;
6. record all visited absolute project paths.

The returned `ProjectMetadataDto` keeps the root project's framework,
web-project flag, direct package references, and direct project references,
then adds:

- `ReferencedProjectPackages`;
- `AllProjectPaths`.

Cycles are safe because absolute paths are tracked case-insensitively. Missing
references are ignored rather than treated as package data. If a referenced
project exists but its XML cannot be read, the scan fails rather than
silently inventing metadata.

## Dependency Analysis and Deployment Defaults

`ProjectScannerService.AnalyzeDependenciesAsync` creates a
`DeploymentConfigDto` for an application and selected `CsProject`:

| Field | Source/default |
|---|---|
| `ProjectId` | `Application.Id` |
| `CsProjectId` | `CsProject.Id` |
| `ProjectName` | `Application.Name` |
| `ExposedPort` | Dynamically allocated loopback port |
| `EnvironmentName` | Development default |
| `Databases` | Empty list before provider matching |

It scans the root project and combines direct package names with packages from
referenced projects. Each rule from `database-providers.json` matches when a
discovered package contains one of the rule's package fragments,
case-insensitively.

For each matching rule, the service adds a `DatabaseConfigDto` with:

- `DbType` from the rule;
- lowercase `ContainerNameSuffix`;
- `<DbType>Connection` as the connection-string name.

Provider matching is intentionally package-fragment based, not an exact
package identity check. Adding a broad fragment can create false positives,
while changing a provider name affects generated container and secret names.

If a non-cancellation exception occurs during dependency analysis, the
baseline configuration is returned and the error is logged. This allows the
Web configuration form to open, but it means callers must not interpret an
empty database list as proof that the project has no database dependency.

## Database Provider Rule Catalog

`database-providers.json` is copied to the application output under the
`Scanner` directory by the `Services.csproj` content rule. The catalog:

- loads lazily on first use;
- caches the parsed list process-wide;
- serializes first-load access with a `SemaphoreSlim`;
- returns an empty list when the file is absent;
- logs and returns an empty list when JSON parsing fails;
- rethrows cancellation.

When adding a provider:

1. add a `DbProviderRuleDto` entry to the JSON file;
2. choose stable `DbType` and package fragments;
3. verify the file is copied to output;
4. check generated database container, connection-string, and secret names;
5. update templates and orchestration if the provider requires special
   credentials or startup behavior.

The catalog is not reloaded during process lifetime. A changed file requires
application restart to take effect.

## Environment Variable Extraction

`ExtractEnvironmentVariablesAsync` merges three sources from the selected
project directory:

1. `appsettings*.json`;
2. parent-directory `.env`, then project-directory `.env`;
3. the first `launchSettings.json` profile whose `commandName` is `Project`.

Later assignments overwrite earlier values. The final dictionary is
case-insensitive. The managed keys `ASPNETCORE_ENVIRONMENT` and
`DOTNET_ENVIRONMENT` are removed because environment selection is controlled
by the deployment configuration.

### JSON flattening

`JsonConfigurationFlattener` converts nested objects and arrays using
double-underscore separators:

```text
ConnectionStrings:DefaultConnection -> ConnectionStrings__DefaultConnection
Logging:LogLevel:Default             -> Logging__LogLevel__Default
AllowedHosts[0]                      -> AllowedHosts__0
```

Scalar JSON values are converted with `JsonElement.ToString()`. Invalid JSON
files are logged and skipped; other source files continue to be processed.

### `.env` parsing

`DotEnvLineParser` accepts:

- `KEY=value`;
- optional `export ` prefix;
- single- or double-quoted values;
- inline comments beginning with a space followed by `#`.

Blank lines, comments, and lines without a non-empty key are ignored. The
parser is intentionally lightweight and does not implement full shell
expansion, multiline values, escaped quote semantics, or variable
interpolation.

Parent `.env` values are loaded before project `.env` values, so the project
file takes precedence.

### Launch settings

Only the first `Project` launch profile is used. Its
`environmentVariables` entries are merged into the result. The scanner also
parses the first HTTP `applicationUrl` port into an internal scan result, but
the current implementation does not apply that port to
`DeploymentConfigDto.ExposedPort`; the dynamic port from `ScannerPortProvider`
remains the effective default.

Environment extraction returns values, not a sanitized secret inventory.
`.env`, appsettings, and launch settings can contain credentials or tokens.
The Web configuration form should display/edit them carefully, and callers
must not log the resulting dictionary.

## Dynamic Port Allocation

`ScannerPortProvider` binds a loopback `TcpListener` to port `0`, reads the
assigned port, then immediately releases the listener. If allocation fails,
it logs the error and returns port `8080`.

This is a best-effort availability check, not a reservation. Another process
can claim the port after the listener closes. Docker Compose or the later
deployment operation remains responsible for handling a port collision.

## Consumers and Boundaries

| Consumer | Scanner responsibility |
|---|---|
| `Web.Components.Pages.LocalGitRepos` | Scans a user-selected local root and displays discovered repositories/projects. |
| `Services.Data.Apps` | Persists selected `LocalProjectDto` and `CsProjectDto` values. |
| `Web.Components.Pages.Dashboard` | Requests dependency analysis before local deployment configuration. |
| `Web.Components.Pages.ProjectDetails` | Requests dependency analysis and project metadata for deployment/editing. |
| `Web.Components.Shared.ConfigurationForm` | Loads extracted environment variables into editable custom variables. |
| `Services.Orchestration` | Uses solution roots, project metadata, database config, and generated deployment defaults. |
| `Services.Templating` | Consumes `ProjectMetadataDto` for Docker/cloud artifact generation. |
| `Core.DTO` | Defines the scanner output contracts. |

Keep filesystem traversal and parsing here. Keep persistence in
`Services.Data`, deployment sequencing in `Services.Orchestration`, generated
file rendering in `Services.Templating`, and user editing in `Web`.

## Error, Cancellation, and Logging Rules

- Validate root and project paths before scanning.
- Pass cancellation tokens through directory traversal, file streams, XML,
  JSON, and rule loading.
- Rethrow `OperationCanceledException`; cancellation is not a scan result.
- Continue past unauthorized or isolated repository-directory failures when
  the operation can safely do so.
- Preserve missing-file exceptions for required root project inputs.
- Avoid logging environment-variable values, package secrets, User Secrets
  identifiers, connection strings, or complete DTOs.
- Treat empty provider rules or empty dependency results as degraded analysis,
  not definitive absence.
- Keep fallback defaults explicit and documented when adding new parsers.

The scanner is intentionally tolerant for interactive discovery but stricter
for required project metadata. Do not make all operations either silently
best-effort or exception-based without preserving the distinction.

## Security and Privacy Rules

- Scanning can read files containing passwords, API keys, connection strings,
  and local development secrets.
- Never log extracted environment dictionaries or full configuration files.
- Do not persist `.env` or appsettings values directly from the scanner.
- Keep `UserSecretsId` as metadata only; do not attempt to read the user
  secrets store here.
- Avoid following reparse points to prevent unexpected traversal outside the
  selected tree.
- Keep repository and project paths in diagnostics only when appropriate for
  the deployment environment.
- Do not execute scripts, MSBuild targets, project code, or package restore as
  part of scanning.

## Testing Guidance

Use temporary directory trees and representative XML/JSON fixtures to test:

- invalid roots and missing project files;
- nested Git repositories and repository boundaries;
- hidden, build, dependency, IDE, and reparse-point exclusions;
- `.sln`, `.slnx`, and `.csproj` detection;
- web SDK detection and parser fallback behavior;
- target framework and package-reference extraction;
- project-reference traversal, cycles, missing references, and duplicate
  packages;
- database rule matching and catalog caching;
- JSON object/array flattening;
- `.env` comments, quotes, exports, precedence, and malformed lines;
- launch profile selection and managed-key removal;
- dynamic port allocation fallback;
- cancellation during directory, XML, JSON, and environment scanning.

Do not use real application secrets in scanner fixtures. Use placeholders and
assert that logs do not contain extracted values.

## Extending the Module

When adding a scanner capability:

1. Decide whether it produces a Core DTO, supports an existing scanner
   operation, or belongs in another module.
2. Keep filesystem access and parsing in focused helpers.
3. Preserve case-insensitive package/environment matching where the source
   semantics require it.
4. Define source precedence before merging new configuration formats.
5. Distinguish missing optional files from missing required project files.
6. Keep traversal bounded and avoid following reparse points.
7. Pass cancellation through all asynchronous I/O.
8. Update the corresponding Core DTO, Web consumer, template/orchestration
   behavior, tests, and documentation together.

Do not add persistence, Docker commands, Azure/GitHub SDK calls, or UI
validation to scanner classes. Scanner output should remain reusable by both
local and cloud deployment workflows.

## File Map

| File | Purpose |
|---|---|
| `ILocalSystemScannerService.cs` | Local repository discovery and solution-root contract. |
| `LocalSystemScannerService.cs` | Recursive Git repository and `.csproj` discovery. |
| `IProjectScannerService.cs` | Project metadata, dependency, and environment extraction contract. |
| `ProjectScannerService.cs` | Coordinates metadata analysis, provider matching, defaults, and environment extraction. |
| `LocalScannerDirectoryRules.cs` | Shared directory exclusion and solution-marker rules. |
| `LocalCsProjectParser.cs` | Lightweight local project DTO parser. |
| `CsprojMetadataReader.cs` | Deployment-relevant `.csproj` XML reader. |
| `ProjectDependencyGraphScanner.cs` | Breadth-first project-reference traversal. |
| `DatabaseProviderRuleCatalog.cs` | Cached rule-file loader. |
| `database-providers.json` | Package fragments mapped to database providers. |
| `ProjectEnvironmentVariableExtractor.cs` | Merges appsettings, launch settings, and `.env` values. |
| `JsonConfigurationFlattener.cs` | Converts nested JSON into environment-style keys. |
| `DotEnvLineParser.cs` | Parses individual `.env` assignments. |
| `ScannerPortProvider.cs` | Best-effort loopback port allocator. |

## Related Documentation

- [`Services`](../README.md)
- [`Services/Data`](../Data/README.md)
- `Services/Templating` when its module README is added
- `Services/Orchestration` when its module README is added
- [`Core/DTO`](../../Core/DTO/README.md)
- [`Core/Entities`](../../Core/Entities/README.md)
- [`Web`](../../Web/README.md)
- [Solution navigation map](../../.agents/navigation.md)
