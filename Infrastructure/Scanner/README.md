# Scanner

Local filesystem and .NET project scanning infrastructure adapters.

Dependency detection logs the project GUID and reviewed database-provider kind; customer names and arbitrary rule
values remain excluded by the platform logging policy.

## Source inventory

- `Projects/CsprojMetadataReader.cs`
- `Catalog/database-providers.json`
- `Catalog/DatabaseProviderRuleCatalog.cs`
- `Environment/DotEnvLineParser.cs`
- `Environment/JsonConfigurationFlattener.cs`
- `Projects/LocalCsProjectParser.cs`
- `Filesystem/LocalScannerDirectoryRules.cs`
- `Filesystem/LocalSystemScannerService.cs`
- `Projects/ProjectDependencyGraphScanner.cs`
- `Environment/ProjectEnvironmentVariableExtractor.cs`
- `Projects/ProjectScannerService.cs`
- `Projects/ScannerPortProvider.cs`

## Boundary

Infrastructure may reference Application and Domain, but never Web. It implements ports and owns provider-specific
behavior.

## Related documentation

- [Solution navigation map](../../navigation.md)

Environment-variable scans propagate filesystem/access failures so deployment forms show safe failure guidance.
Successfully scanned empty configuration remains an empty result; malformed optional JSON is still skipped as before.

## Folder organization

Folders group related files while retaining the module's existing .NET namespaces and public contracts.

- `Catalog/` — `Catalog/DatabaseProviderRuleCatalog.cs`, `Catalog/database-providers.json`.
- `Environment/` — `Environment/DotEnvLineParser.cs`, `Environment/JsonConfigurationFlattener.cs`,
  `Environment/ProjectEnvironmentVariableExtractor.cs`.
- `Filesystem/` — `Filesystem/LocalScannerDirectoryRules.cs`, `Filesystem/LocalSystemScannerService.cs`.
- `Projects/` — `Projects/CsprojMetadataReader.cs`, `Projects/LocalCsProjectParser.cs`,
  `Projects/ProjectDependencyGraphScanner.cs`, `Projects/ProjectScannerService.cs`, `Projects/ScannerPortProvider.cs`.
