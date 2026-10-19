# .NET metadata analysis

SDK-style project parsing, reference-graph package analysis, database configuration and local port selection.

## Contents

| File                                                                 | Types or asset                                       | Responsibility                                                                                      |
|----------------------------------------------------------------------|------------------------------------------------------|-----------------------------------------------------------------------------------------------------|
| [CsprojMetadataReader.cs](CsprojMetadataReader.cs)                   | `CsprojMetadataReader`                               | Reads deployment-relevant metadata from SDK-style C# project files.                                 |
| [LocalCsProjectParser.cs](LocalCsProjectParser.cs)                   | `LocalCsProjectParser`                               | Parses local C# project files discovered during repository scans.                                   |
| [ProjectDependencyGraphScanner.cs](ProjectDependencyGraphScanner.cs) | `ProjectDependencyGraphScanner`, `ProjectScanTarget` | Walks project references from a root .csproj file and combines referenced project metadata.         |
| [ProjectScannerService.cs](ProjectScannerService.cs)                 | `ProjectScannerService`                              | Coordinates C# project metadata scanning, dependency analysis, and environment-variable extraction. |
| [ScannerPortProvider.cs](ScannerPortProvider.cs)                     | `ScannerPortProvider`                                | Allocates available local ports for generated local deployment configurations.                      |

## Usage and integration

CsprojMetadataReader reads framework/packages/references/UserSecretsId. ProjectDependencyGraphScanner walks references
and combines transitive metadata; LocalCsProjectParser supplies discovery DTOs. ProjectScannerService coordinates
metadata, catalog-based database detection and environment extraction. ScannerPortProvider finds an available local
port.

## Contracts and lifecycle

Direct and referenced-project packages remain distinct; use case-insensitive path/package identities as defined by
Domain DTOs. UserSecretsId identifies a store, not its secret values. Parsing project XML is not a complete MSBuild
evaluation or a deployment-success guarantee.

Port availability is a point-in-time observation, not a reservation: local launch performs conflict checks before side
effects. Prepared configuration can contain secret environment/database values. Keep package/provider detection logs
finite and forward cancellation into graph/file reads.

## Related documentation

- [Parent module](../README.md)
- [Port](../../../Application/Abstractions/Scanning/README.md)
- [Metadata shapes](../../../Domain/DTO/Projects/README.md)
- [Provider rules](../Catalog/README.md)
- [Configuration extraction](../Environment/README.md)
- [Launch-time conflict tests](../../../Test/Infra/Orchestration/LocalHostPortAvailabilityTests.cs)
