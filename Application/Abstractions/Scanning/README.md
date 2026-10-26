# Project scanning ports

Local discovery and .NET project/dependency/environment extraction without exposing scanner implementation types.

## Contents

| File                                                           | Types                        | Responsibility                                                                                                                       |
|----------------------------------------------------------------|------------------------------|--------------------------------------------------------------------------------------------------------------------------------------|
| [ILocalSystemScannerService.cs](ILocalSystemScannerService.cs) | `ILocalSystemScannerService` | Service interface for scanning the local file system to identify Git projects.                                                       |
| [IProjectScannerService.cs](IProjectScannerService.cs)         | `IProjectScannerService`     | Service responsible for analyzing .csproj files to extract metadata such as the target .NET version, project type, and dependencies. |

## Usage and integration

ILocalSystemScannerService finds repository/solution folders and resolves solution roots. IProjectScannerService reads
project metadata, derives deployment configuration from application/C# project models and extracts environment values.
Infrastructure provides filesystem, XML, package-rule and configuration parsing.

## Contracts and lifecycle

ScanProjectContentAsync returns Domain project metadata; AnalyzeDependenciesAsync returns prepared deployment
configuration. Discovery alone does not authorize filesystem access or establish deployability. Extracted environment
values may contain secrets. Preserve cancellation and distinguish an empty successful scan from a filesystem/access
failure.

## Related documentation

- [Parent module](../README.md)
- [Local scanner](../../../Infrastructure/Scanner/Filesystem/LocalSystemScannerService.cs)
- [Project scanner](../../../Infrastructure/Scanner/Projects/ProjectScannerService.cs)
- [Scanner organization](../../../Infrastructure/Scanner/README.md)
- [Discovery projections](../../../Domain/DTO/Projects/README.md)
- [Configuration output](../../../Domain/DTO/Deployment/README.md)
