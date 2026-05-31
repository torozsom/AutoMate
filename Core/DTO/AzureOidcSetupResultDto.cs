namespace Core.DTO;

/// <summary>
///     Contains Azure OIDC setup details that must be provided to GitHub Actions.
/// </summary>
public record AzureOidcSetupResultDto
{
    /// <summary>
    ///     The Azure client ID used by GitHub Actions OIDC login.
    /// </summary>
    public string ClientId { get; init; } = string.Empty;

    /// <summary>
    ///     The Microsoft Entra tenant ID used by GitHub Actions OIDC login.
    /// </summary>
    public string TenantId { get; init; } = string.Empty;

    /// <summary>
    ///     The Azure subscription ID used by GitHub Actions OIDC login.
    /// </summary>
    public string SubscriptionId { get; init; } = string.Empty;

    /// <summary>
    ///     The Azure resource ID of the user-assigned managed identity used by GitHub Actions.
    /// </summary>
    public string IdentityResourceId { get; init; } = string.Empty;

    /// <summary>
    ///     The federated credential name configured on the managed identity.
    /// </summary>
    public string FederatedCredentialName { get; init; } = string.Empty;

    /// <summary>
    ///     The GitHub OIDC issuer trusted by the federated credential.
    /// </summary>
    public string Issuer { get; init; } = string.Empty;

    /// <summary>
    ///     The GitHub OIDC subject trusted by the federated credential.
    /// </summary>
    public string Subject { get; init; } = string.Empty;

    /// <summary>
    ///     The GitHub OIDC audience trusted by the federated credential.
    /// </summary>
    public string Audience { get; init; } = string.Empty;
}
