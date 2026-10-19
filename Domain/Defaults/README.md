# Deployment defaults

Shared constants used to seed deployment DTOs and prepare generated artifacts.

## Contents

| File                                           | Responsibility                                                                        |
|------------------------------------------------|---------------------------------------------------------------------------------------|
| [DeploymentDefaults.cs](DeploymentDefaults.cs) | DeploymentDefaults: environment, port, region, database and cloud-artifact constants. |

## Usage and integration

Use DeploymentDefaults instead of repeating initial values. [Deployment DTOs](../DTO/Deployment/README.md) apply them in
property
initializers. [CloudDeploymentDefaults](../../Infrastructure/ApplicationServices/Orchestration/CloudPreparation/CloudDeploymentDefaults.cs)
applies cloud preparation choices. [Templating](../../Infrastructure/Templating/README.md) turns prepared configuration
into artifacts.

## Contracts and lifecycle

Local configuration starts with Development and port 8080; cloud preparation can choose Production. The initial Azure
region is eastus. Database names, users, connection-string keys and container suffixes are template seeds, not
discovered infrastructure settings.

The database-password constant is a development placeholder, not a production credential policy. Configured database
values require secret handling. Cloud branch/workflow defaults identify generated artifacts; changes can affect dispatch
and workflow matching.

## Related documentation

- [Domain](../README.md)
- [Deployment request contracts](../DTO/Deployment/README.md)
- [Cloud template regressions](../../Test/Infra/Templating/CloudRegistryTemplateTests.cs)
