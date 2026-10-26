# Project classifications

Application kind and source location used by imported/scanned project models.

## Contents

| File                           | Types and responsibility                       |
|--------------------------------|------------------------------------------------|
| [AppType.cs](AppType.cs)       | AppType: WebApi, Blazor or Mvc classification. |
| [SourceType.cs](SourceType.cs) | SourceType: Local or Remote source location.   |

## Usage and integration

[Application](../../Entities/Projects/Application.cs) stores both
classifications. [ApplicationService](../../../Infrastructure/ApplicationServices/Data/Apps/ApplicationService.cs)
handles imported project state. Use the source classification when selecting source-specific workflows; use AppType when
interpreting supported application kinds.

## Contracts and compatibility

These values do not validate filesystem paths, repository permissions or deployment capability. WebApi has numeric value
zero and is the CLR default; an unset AppType does not demonstrate successful detection. Keep existing order/values
stable for persisted project data.

## Related documentation

- [Enums index](../README.md)
- [Project hierarchy](../../Entities/Projects/README.md)
- [Discovery projections](../../DTO/Projects/README.md)
- [Source-aware workflow tests](../../../Test/Web/Projects/ProjectDetailsStopTests.cs)
