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
}
