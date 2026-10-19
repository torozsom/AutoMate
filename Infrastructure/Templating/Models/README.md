# Template data and naming

Scriban model assembly, deterministic resource names and matching cloud secret-name conventions.

## Contents

| File                                                           | Types or asset                                                             | Responsibility                                                                                 |
|----------------------------------------------------------------|----------------------------------------------------------------------------|------------------------------------------------------------------------------------------------|
| [CloudDeploymentSecretNames.cs](CloudDeploymentSecretNames.cs) | `CloudDeploymentSecretNames`                                               | Builds deterministic GitHub Actions secret names used by generated cloud deployment workflows. |
| [TemplateModelFactory.cs](TemplateModelFactory.cs)             | `TemplateModelFactory`, `MainProjectTemplateInfo`, `CloudNameTemplateInfo` | Builds the anonymous Scriban model consumed by AutoMate deployment templates.                  |
| [TemplateNameNormalizer.cs](TemplateNameNormalizer.cs)         | `TemplateNameNormalizer`                                                   | Normalizes names used in generated Docker, Azure, and Container Apps resources.                |

## Usage and integration

TemplateModelFactory combines DeploymentConfigDto, ProjectMetadataDto, project name and output anchor into the anonymous
model consumed by all templates. TemplateNameNormalizer handles resource/database/secret segments. Public
CloudDeploymentSecretNames is shared with cloud preparation so uploaded secrets match generated workflow references.

## Contracts and lifecycle

Model shape is a contract with Scriban properties: change model, templates, manifest and tests together. Normalize
aliases/lengths without silently changing deployed identities. Database login requirements differ by provider. Secret
names use deterministic indexed/hash segments with existing bounds; upload and template code must use the same mapping.

Model/configuration values may include credentials even when resource tags/history are non-secret. Never serialize the
whole model into telemetry. Project-relative paths and selected runtime/package metadata are inputs, not a complete
build evaluation or proof of provider availability.

## Related documentation

- [Parent module](../README.md)
- [Model consumers](../Templates/README.md)
- [Secret-name consumer](../../ApplicationServices/Orchestration/CloudPreparation/CloudRepositorySecretBuilder.cs)
- [Inputs](../../../Domain/DTO/Deployment/README.md)
- [Model/template regressions](../../../Test/Infra/Templating/CloudRegistryTemplateTests.cs)
- [Ownership regressions](../../../Test/Infra/Templating/LocalOwnershipTemplateTests.cs)
