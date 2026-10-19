# Template selection and paths

Cached manifest rules, Local/Cloud activation and validated catalog/output path resolution.

## Contents

| File                                                     | Types or asset            | Responsibility                                                                       |
|----------------------------------------------------------|---------------------------|--------------------------------------------------------------------------------------|
| [TemplateManifestCatalog.cs](TemplateManifestCatalog.cs) | `TemplateManifestCatalog` | Loads and caches template manifest rules used by the templating service.             |
| [TemplatePaths.cs](TemplatePaths.cs)                     | `TemplatePaths`           | Centralizes template and generated-output path resolution with traversal protection. |
| [TemplateRuleMatcher.cs](TemplateRuleMatcher.cs)         | `TemplateRuleMatcher`     | Applies manifest rule activation and deployment-target matching.                     |

## Usage and integration

TemplateManifestCatalog loads copied template-manifest.json; TemplateRuleMatcher checks active/target compatibility.
TemplatePaths anchors runtime assets at AppContext.BaseDirectory/Templating/Templates and normalizes relative outputs.
Rendering calls these helpers before loading/writing files.

## Contracts and lifecycle

Targets are All, Local and Cloud. Empty/rooted/parent-traversal output names are rejected; resolved template/output
paths must stay under their intended roots. The output-directory anchor can represent repository root or .automate;
preserve existing root resolution so cloud project references remain correct.

A missing/empty manifest can yield no generated artifacts under existing behavior; it does not imply a valid deployment.
Keep source assets and Infrastructure.csproj copy rules aligned. Cached rule changes need the existing process lifecycle
to reload; do not assume per-request file watching.

## Related documentation

- [Parent module](../README.md)
- [Rule DTOs](../../../Domain/DTO/Templating/README.md)
- [Consumer](../Rendering/README.md)
- [Manifest/assets](../Templates/README.md)
- [Local outputs](../../../Test/Infra/Templating/LocalOwnershipTemplateTests.cs)
- [Cloud outputs](../../../Test/Infra/Templating/CloudRegistryTemplateTests.cs)
