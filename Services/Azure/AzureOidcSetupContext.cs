namespace Services.Azure;

/// <summary>
///     Derived Azure OIDC setup names and claims used throughout one provisioning operation.
/// </summary>
/// <param name="ResourceGroupName">The Azure resource group that owns the deployment infrastructure.</param>
/// <param name="Subject">The GitHub branch subject claim trusted by the federated credential.</param>
/// <param name="IdentityName">The deterministic user-assigned managed identity name.</param>
/// <param name="FederatedCredentialName">The deterministic federated credential name.</param>
internal sealed record AzureOidcSetupContext(
    string ResourceGroupName,
    string Subject,
    string IdentityName,
    string FederatedCredentialName);