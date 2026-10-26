# Azure deployment provisioning

Required resource-provider registration, GitHub federation setup and customer-owned ACR preparation.

## Contents

| File                                                                         | Types or asset                      | Responsibility                                                                                     |
|------------------------------------------------------------------------------|-------------------------------------|----------------------------------------------------------------------------------------------------|
| [AzureContainerRegistryProvisioner.cs](AzureContainerRegistryProvisioner.cs) | `AzureContainerRegistryProvisioner` | Ensures a customer-owned Basic ACR exists in the deployment resource group.                        |
| [AzureDeploymentOrchestrator.cs](AzureDeploymentOrchestrator.cs)             | `AzureDeploymentOrchestrator`       | Uses Azure Resource Manager to prepare OIDC trust for GitHub Actions cloud deployments.            |
| [AzureResourceProviderRegistrar.cs](AzureResourceProviderRegistrar.cs)       | `AzureResourceProviderRegistrar`    | Ensures Azure resource providers required by the selected deployment configuration are registered. |

## Usage and integration

AzureDeploymentOrchestrator implements IAzureDeploymentOrchestrator and coordinates Identity helpers.
AzureResourceProviderRegistrar ensures providers needed by the selected configuration, including Microsoft.Insights
before publication. AzureContainerRegistryProvisioner ensures the customer Basic registry and returns its verified
server identity.

## Contracts and lifecycle

Use current ARM credentials and the selected subscription/resource group; customer permission/propagation can delay
registration or role readiness. Setup uses deterministic identities/trust and separate ACR push/pull permissions. Never
infer successful resource provisioning from generated templates or substitute a different account silently.

Secrets remain in protected connection/configuration handling; logs omit tokens, OIDC payloads and provider bodies.
Preserve cancellation, readiness polling and idempotent existing-resource checks. Deployment preparation routes safe
failures to its recorded deployment.

## Related documentation

- [Parent module](../README.md)
- [Identity helpers](../Identity/README.md)
- [Consumer](../../ApplicationServices/Orchestration/CloudPreparation/README.md)
- [Registration tests](../../../Test/Infra/Azure/Provisioning/AzureResourceProviderRegistrarTests.cs)
- [Registry artifacts](../../../Test/Infra/Templating/CloudRegistryTemplateTests.cs)
