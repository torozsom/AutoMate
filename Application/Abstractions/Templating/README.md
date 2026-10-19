# Artifact generation port

Deployment artifact rendering and optional filesystem writing from shared configuration and metadata.

## Contents

| File                                           | Types                | Responsibility                                                                                                                                     |
|------------------------------------------------|----------------------|----------------------------------------------------------------------------------------------------------------------------------------------------|
| [ITemplatingService.cs](ITemplatingService.cs) | `ITemplatingService` | Service responsible for dynamically generating infrastructure-as-code files (like Dockerfile, docker-compose.yml) based on project configurations. |

## Usage and integration

ITemplatingService offers GenerateAllTemplatesAsync for generated TemplateFile results and
GenerateAndSaveAllTemplatesAsync for rendering plus writing. Both take deployment settings, scanner metadata, selected
C# project name and an output-directory anchor. Local and cloud orchestrators use the results for Docker assets or
repository commits.

## Contracts and lifecycle

Generated paths are relative artifact destinations; Infrastructure owns manifest selection, rendering and path safety.
Generation is not deployment execution. Content can embed configured credentials, so avoid logging rendered files.
Propagate cancellation into rendering/writes and preserve target-specific template selection.

## Related documentation

- [Parent module](../README.md)
- [Generation implementation](../../../Infrastructure/Templating/Rendering/TemplatingService.cs)
- [Templates and rules](../../../Infrastructure/Templating/README.md)
- [Artifact DTOs](../../../Domain/DTO/Templating/README.md)
- [Local templates](../../../Test/Infra/Templating/LocalOwnershipTemplateTests.cs)
- [Cloud templates](../../../Test/Infra/Templating/CloudRegistryTemplateTests.cs)
