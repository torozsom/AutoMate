# Scriban rendering and output

Artifact generation coordination, template parsing and validated filesystem writing.

## Contents

| File                                                     | Types or asset            | Responsibility                                                                        |
|----------------------------------------------------------|---------------------------|---------------------------------------------------------------------------------------|
| [ScribanTemplateRenderer.cs](ScribanTemplateRenderer.cs) | `ScribanTemplateRenderer` | Parses and renders individual Scriban templates.                                      |
| [TemplateFileWriter.cs](TemplateFileWriter.cs)           | `TemplateFileWriter`      | Writes generated template files to disk with output path safety checks.               |
| [TemplatingService.cs](TemplatingService.cs)             | `TemplatingService`       | Coordinates Scriban template generation for local Docker and cloud deployment assets. |

## Usage and integration

TemplatingService implements ITemplatingService. GenerateAllTemplatesAsync loads selected rules, builds the model and
returns TemplateFile results; GenerateAndSaveAllTemplatesAsync additionally invokes TemplateFileWriter.
ScribanTemplateRenderer loads/parses/renders one rule through catalog path validation.

## Contracts and lifecycle

Missing runtime template directory throws; an empty/unavailable manifest returns an empty result with existing guidance.
A missing individual template is skipped; parse errors fail rather than silently return malformed content. Preserve
these distinctions so callers do not equate any completion with full artifact availability.

Normalize relative output paths before returning and recheck containment during writes. File operations accept
cancellation; do not claim rendering itself starts a deployment or validates generated provider configuration. Log
reviewed artifact names, not paths/content/credentials. Caller-selected cloud publication remains separate from disk
writing.

## Related documentation

- [Parent module](../README.md)
- [Generation port](../../../Application/Abstractions/Templating/README.md)
- [Path/rule validation](../Catalog/README.md)
- [Model data](../Models/README.md)
- [Local regressions](../../../Test/Infra/Templating/LocalOwnershipTemplateTests.cs)
- [Cloud regressions](../../../Test/Infra/Templating/CloudRegistryTemplateTests.cs)
