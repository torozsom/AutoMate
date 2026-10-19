# Deployment template assets

Runtime Scriban templates and manifest destinations for local Docker and cloud GitHub/Azure artifacts.

## Contents

| File                                                     | Types or asset | Responsibility                                                                      |
|----------------------------------------------------------|----------------|-------------------------------------------------------------------------------------|
| [azure-aca.bicep.scriban](azure-aca.bicep.scriban)       | Runtime asset  | Azure Container Apps, identities, databases and diagnostic export infrastructure.   |
| [docker-compose.scriban](docker-compose.scriban)         | Runtime asset  | Local web/database services, configuration and ownership labels.                    |
| [Dockerfile.scriban](Dockerfile.scriban)                 | Runtime asset  | Multi-stage .NET container build template.                                          |
| [dockerignore.scriban](dockerignore.scriban)             | Runtime asset  | Build-context exclusions rendered as Dockerfile.dockerignore.                       |
| [github-actions.yml.scriban](github-actions.yml.scriban) | Runtime asset  | Cloud build/provision/deploy workflow, including ACR/OIDC and legacy GHCR branches. |
| [template-manifest.json](template-manifest.json)         | Runtime asset  | Enabled Local/Cloud rules and relative generated artifact destinations.             |

## Usage and integration

template-manifest.json selects local Dockerfile, Dockerfile.dockerignore and docker-compose.yml; cloud output includes
.automate/Dockerfile, .automate/Dockerfile.dockerignore, infra/main.bicep and .github/workflows/deploy.yml.
Infrastructure.csproj copies these assets to the runtime template directory; Rendering and Models provide
selection/data.

## Contracts and lifecycle

Local Compose labels bind daemon/log ownership to project/service/deployment; database service identity remains stable
across redeployments. Preserve AUTOMATE_DEPLOYMENT_ID injection from DockerCli rather than embed a guessed deployment
ID. Docker ignore output matches the selected Dockerfile name.

ACR workflows use Azure OIDC and separate push/pull managed identities; the legacy GHCR branch remains. Infrastructure
tags web/database resources for exact discovery and exports supported operational categories through automate-runtime
settings. MySQL/Redis remain metrics-only by default; audit/slow-query logging is customer-controlled.

Workflow deploys and explicitly starts an app without replacing scale/network policy. Templates may interpolate
secret-bearing inputs/references; avoid logging rendered contents. Template/model/secret-name/manifest changes must be
reviewed together and applied to deployed infrastructure before new collection tags/settings can take effect.

## Related documentation

- [Parent module](../README.md)
- [Runtime copy rules](../../Infrastructure.csproj)
- [Scriban contract](../Models/README.md)
- [Generation](../Rendering/README.md)
- [Ownership/export consumers](../../Azure/Databases/README.md)
- [Local artifact tests](../../../Test/Infra/Templating/LocalOwnershipTemplateTests.cs)
- [Cloud artifact tests](../../../Test/Infra/Templating/CloudRegistryTemplateTests.cs)
