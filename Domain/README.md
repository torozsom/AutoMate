# Domain

Shared business data and contracts for AutoMate: project ownership, deployment history, cloud execution, diagnostics and
AI analysis. This .NET 10/C# 14 project enables nullable reference types and implicit usings, with no project or package
references.

## Contents

| File or module                 | Responsibility                            |
|--------------------------------|-------------------------------------------|
| [Domain.csproj](Domain.csproj) | Framework and compiler settings.          |
| [Defaults](Defaults/README.md) | Deployment request and artifact defaults. |
| [DTO](DTO/README.md)           | Shared workflow inputs and projections.   |
| [Entities](Entities/README.md) | Persisted business and execution state.   |
| [Enums](Enums/README.md)       | Classifications and lifecycle values.     |

## Usage and integration

Reference Domain when a workflow or adapter needs a shared model. Choose entities for persisted state, DTOs for workflow
inputs/results, enums for classifications, and defaults when preparing requests. Follow the module indexes above to
locate individual classes and their consumers.

Dependencies point inward: Application references Domain; Infrastructure implements persistence and provider behavior;
Web composes and presents workflows. Domain performs no database, filesystem, HTTP or provider SDK operations. Some DTOs
use BCL serialization metadata, such as GitHub JSON property names.

## Contracts and maintenance

Models are mostly data containers. Constructing one does not enforce authorization, validation, redaction, encryption or
lifecycle transitions; those guarantees belong to the linked
consumers. [Infrastructure Data](../Infrastructure/Data/README.md) owns persistence mappings and audit handling.

Preserve public namespaces, serialized shapes, default values and persisted enum representations. Keep folder
inventories and consumer links current. Use concise XML documentation for declarations, including parameters,
nullability, ownership and lifecycle details where meaningful; do not imply guarantees a model does not enforce.

## Related documentation

- [Solution navigation](../navigation.md)
- [Application contracts](../Application/README.md)
- [Infrastructure implementations](../Infrastructure/README.md)
- [Web presentation](../Web/README.md)
- [Tests](../Test/README.md)
