# Azure identity and authorization

Fresh ARM credentials, managed identities, deterministic GitHub OIDC trust and scoped RBAC setup.

## Contents

| File                                                                     | Types or asset                                                     | Responsibility                                                                               |
|--------------------------------------------------------------------------|--------------------------------------------------------------------|----------------------------------------------------------------------------------------------|
| [AzureArmCredentialsProvider.cs](AzureArmCredentialsProvider.cs)         | `AzureArmCredentialsProvider`                                      | Refreshes Azure Resource Manager credentials when a cloud launch starts.                     |
| [AzureFederatedCredentialService.cs](AzureFederatedCredentialService.cs) | `AzureFederatedCredentialService`, `FederatedCredentialProperties` | Creates and verifies Azure managed identity federated credentials for GitHub Actions OIDC.   |
| [AzureManagedIdentityProvisioner.cs](AzureManagedIdentityProvisioner.cs) | `AzureManagedIdentityProvisioner`                                  | Provisions Azure resource groups and user-assigned managed identities through the Azure SDK. |
| [AzureOidcSetupContext.cs](AzureOidcSetupContext.cs)                     | `AzureOidcSetupContext`                                            | Derived Azure OIDC setup names and claims used throughout one provisioning operation.        |
| [AzureOidcSetupPlanner.cs](AzureOidcSetupPlanner.cs)                     | `AzureOidcSetupPlanner`                                            | Builds deterministic Azure identity names and GitHub OIDC claims for cloud deployments.      |
| [AzureRoleAssignmentService.cs](AzureRoleAssignmentService.cs)           | `AzureRoleAssignmentService`                                       | Assigns Azure RBAC permissions needed by the deployment managed identity.                    |
| [StaticAccessTokenCredential.cs](StaticAccessTokenCredential.cs)         | `StaticAccessTokenCredential`                                      | Adapts an already-issued OAuth access token for Azure SDK calls during the current request.  |

## Usage and integration

AzureArmCredentialsProvider refreshes ARM tokens from the protected RemoteUser connection at launch.
StaticAccessTokenCredential adapts already-issued tokens for request-scoped SDK calls. AzureOidcSetupPlanner/Context
derive identity names/claims; managed-identity, federation and role services create/verify the trust and assignments.

## Contracts and lifecycle

ARM access differs from Monitor Logs data-plane scope. Persist rotated account tokens through protected mappings; never
log token/identity/subject payloads. StaticAccessTokenCredential does not itself refresh a token. Customer delegated
permissions must permit resource-group/identity/RBAC operations.

Federated trust binds exact repository/branch issuer/subject/audience. ACR uses separate push and pull identities;
Container Apps uses the pull identity. Preserve deterministic names and propagation/readiness checks so retries verify
existing resources rather than grant broader permissions. Failure logs retain attempt/type metadata instead of raw
provider bodies.

## Related documentation

- [Parent module](../README.md)
- [Ports](../../../Application/Abstractions/Azure/README.md)
- [Protected connection](../../../Domain/Entities/Users/README.md)
- [Setup orchestration](../Provisioning/README.md)
- [Refresh tests](../../../Test/Infra/Azure/Provisioning/AzureArmCredentialsProviderTests.cs)
- [Identity/template tests](../../../Test/Infra/Templating/CloudRegistryTemplateTests.cs)
