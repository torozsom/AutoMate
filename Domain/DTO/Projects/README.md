# Project discovery contracts

Filesystem discovery and .NET metadata projections used before or during deployment preparation.

## Contents

| File                                           | Types and responsibility                                                                     |
|------------------------------------------------|----------------------------------------------------------------------------------------------|
| [CsProjectDto.cs](CsProjectDto.cs)             | CsProjectDto: discovered .csproj name, full path and web-project classification.             |
| [LocalProjectDto.cs](LocalProjectDto.cs)       | LocalProjectDto: scanned repository/solution folder and its discovered C# projects.          |
| [ProjectMetadataDto.cs](ProjectMetadataDto.cs) | ProjectMetadataDto: framework/runtime, User Secrets ID, packages and dependency-graph paths. |
| [DbProviderRuleDto.cs](DbProviderRuleDto.cs)   | DbProviderRuleDto: database classification and matching package-name fragments.              |

## Usage and integration

[LocalSystemScannerService](../../../Infrastructure/Scanner/Filesystem/LocalSystemScannerService.cs) produces
folder/project discoveries. [ProjectScannerService](../../../Infrastructure/Scanner/Projects/ProjectScannerService.cs)
reads framework and dependency
metadata. [DatabaseProviderRuleCatalog](../../../Infrastructure/Scanner/Catalog/DatabaseProviderRuleCatalog.cs) loads
detection rules. Deployment preparation consumes these results without requiring scanner implementation types.

## Contracts and lifecycle

ProjectMetadataDto separates direct packages from packages in referenced projects and records absolute paths across the
dependency graph. Package maps and the path set use case-insensitive comparers. UserSecretsId identifies a development
secret store; it is not secret-store content.

Paths, references and database detections are descriptive inputs, not filesystem authorization or proof a database
exists. Persisted project identities belong to Entities. Collections can remain mutable even where properties use init
accessors.

## Related documentation

- [DTO index](../README.md)
- [Scanner guide](../../../Infrastructure/Scanner/README.md)
- [Persisted project hierarchy](../../Entities/Projects/README.md)
- [Deployment consumers](../Deployment/README.md)
