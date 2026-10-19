# Deployment artifact generation

Manifest-driven Scriban rendering for local Docker and cloud GitHub/Azure deployments.

## Contents

| Module                           | Responsibility                                                      |
|----------------------------------|---------------------------------------------------------------------|
| [Catalog](Catalog/README.md)     | Manifest caching, target matching and path safety.                  |
| [Models](Models/README.md)       | Scriban models, naming rules and cloud secret-name conventions.     |
| [Rendering](Rendering/README.md) | Generation coordination, Scriban rendering and safe output writing. |
| [Templates](Templates/README.md) | Runtime Scriban assets and Local/Cloud output manifest.             |

## Usage and integration

Catalog loads/selects rules and validates paths. Models creates render data and deterministic names/secret keys.
Rendering returns TemplateFile results or safely writes them. Templates holds deployed runtime assets copied by
Infrastructure.csproj. Local orchestration writes generated files; cloud preparation commits them to GitHub.

## Contracts and lifecycle

Local Compose uses ownership labels for project/service/deployment correlation. ACR workflows use Azure OIDC plus
separate push/pull identities; legacy GHCR support remains. Azure infrastructure includes ownership tags and operational
diagnostic export categories, not blanket database audit/slow-query enablement.

Generated content and model inputs can contain secrets. Log reviewed filenames, not full paths/content. Changes to model
properties, manifest destinations, templates and repository secret names must remain coordinated; generation alone does
not validate a successful deployment.

## Related documentation

- [Parent module](../README.md)
- [Generation port](../../Application/Abstractions/Templating/README.md)
- [Artifact DTOs](../../Domain/DTO/Templating/README.md)
- [Local regressions](../../Test/Infra/Templating/LocalOwnershipTemplateTests.cs)
- [Cloud regressions](../../Test/Infra/Templating/CloudRegistryTemplateTests.cs)
