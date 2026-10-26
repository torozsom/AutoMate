# Template artifact contracts

Manifest selection rules and rendered file results exchanged by deployment artifact generation.

## Contents

| File                                                     | Types and responsibility                                                                             |
|----------------------------------------------------------|------------------------------------------------------------------------------------------------------|
| [TemplateManifestRuleDto.cs](TemplateManifestRuleDto.cs) | TemplateManifestRuleDto: catalog template, relative output path, deployment target and enabled flag. |
| [TemplateFile.cs](TemplateFile.cs)                       | TemplateFile: relative output path and rendered artifact content.                                    |

## Usage and integration

[TemplateManifestCatalog](../../../Infrastructure/Templating/Catalog/TemplateManifestCatalog.cs) loads
rules; [TemplateRuleMatcher](../../../Infrastructure/Templating/Catalog/TemplateRuleMatcher.cs) selects
artifacts. [ScribanTemplateRenderer](../../../Infrastructure/Templating/Rendering/ScribanTemplateRenderer.cs) renders
content and [TemplateFileWriter](../../../Infrastructure/Templating/Rendering/TemplateFileWriter.cs) writes files. Cloud
orchestration can commit artifacts to a repository instead.

## Contracts and lifecycle

Manifest rules default to DeploymentTarget = All and IsActive = true; supported targets are All, Local and Cloud.
TemplateFile.Path is relative to the output root. Output-path safety is enforced by Infrastructure's resolver/writer,
not the DTO. Rendered content may contain configured secrets; do not log whole generated files.

## Related documentation

- [DTO index](../README.md)
- [Templating guide](../../../Infrastructure/Templating/README.md)
- [Rendering inputs](../Deployment/README.md)
- [Local artifact tests](../../../Test/Infra/Templating/LocalOwnershipTemplateTests.cs)
- [Cloud artifact tests](../../../Test/Infra/Templating/CloudRegistryTemplateTests.cs)
