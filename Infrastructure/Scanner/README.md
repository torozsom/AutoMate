# Scanner

Local filesystem and .NET project scanning infrastructure adapters.

## Source inventory

- `CsprojMetadataReader.cs`
- `database-providers.json`
- `DatabaseProviderRuleCatalog.cs`
- `DotEnvLineParser.cs`
- `JsonConfigurationFlattener.cs`
- `LocalCsProjectParser.cs`
- `LocalScannerDirectoryRules.cs`
- `LocalSystemScannerService.cs`
- `ProjectDependencyGraphScanner.cs`
- `ProjectEnvironmentVariableExtractor.cs`
- `ProjectScannerService.cs`
- `ScannerPortProvider.cs`

## Boundary

Infrastructure may reference Application and Domain, but never Web. It implements ports and owns provider-specific behavior.

## Related documentation

- [Solution navigation map](../../.agents/navigation.md)
