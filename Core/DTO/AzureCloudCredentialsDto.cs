namespace Core.DTO;

/// <summary>
///     Contains Azure account details required for cloud deployment setup.
/// </summary>
public record AzureCloudCredentialsDto
{
    /// <summary>
    ///     The Microsoft Entra tenant ID connected to the current user.
    /// </summary>
    public string TenantId { get; init; } = string.Empty;

    /// <summary>
    ///     The Azure subscription ID that should host deployment resources.
    /// </summary>
    public string SubscriptionId { get; init; } = string.Empty;

    /// <summary>
    ///     The OAuth access token used to call Azure Resource Manager APIs.
    /// </summary>
    public string AccessToken { get; init; } = string.Empty;
}