# Database detection catalog

Cached package-fragment rules used when deriving deployment database configuration.

## Contents

| File                                                             | Types or asset                | Responsibility                                                                                 |
|------------------------------------------------------------------|-------------------------------|------------------------------------------------------------------------------------------------|
| [database-providers.json](database-providers.json)               | Runtime asset                 | Package-fragment rules for PostgreSQL, MySQL, SQL Server, MongoDB, Redis and Oracle detection. |
| [DatabaseProviderRuleCatalog.cs](DatabaseProviderRuleCatalog.cs) | `DatabaseProviderRuleCatalog` | Loads and caches database provider package matching rules from scanner configuration.          |

## Usage and integration

DatabaseProviderRuleCatalog loads database-providers.json into DbProviderRuleDto values for ProjectScannerService. The
source catalog covers PostgreSQL, MySQL, SQL Server, MongoDB, Redis and Oracle package families. Infrastructure.csproj
copies it under Scanner/database-providers.json at runtime.

## Contracts and lifecycle

Keep source and copied runtime location aligned. Detection matches configured package fragments; it does not discover a
live database or guarantee provider support for every deployment target. Cloud validation has its own supported-engine
subset. Preserve existing cache/failure behavior and avoid arbitrary detected values in external telemetry.

## Related documentation

- [Parent module](../README.md)
- [Rule consumer](../Projects/README.md)
- [Rule shape](../../../Domain/DTO/Projects/README.md)
- [Target validation](../../../Application/Orchestration/Cloud/README.md)
- [Asset copy rule](../../Infrastructure.csproj)
