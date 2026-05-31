namespace Core.Entities;

/// <summary>
///     Represents a user authenticated through a remote source-code provider.
/// </summary>
public class RemoteUser : User
{
    /// <summary>
    ///     Gets or sets the unique identifier of the user on the remote provider.
    /// </summary>
    public required string AccountId { get; set; }

    /// <summary>
    ///     Gets or sets the URL of the user's avatar image on the remote provider.
    /// </summary>
    public string? AvatarUrl { get; set; }

    /// <summary>
    ///     Gets or sets the GitHub access token associated with the user, if available.
    /// </summary>
    public string? GitHubAccessToken { get; set; }

    /// <summary>
    ///     Gets or sets the unique Microsoft Entra account identifier for a connected Azure account.
    /// </summary>
    public string? AzureAccountId { get; set; }

    /// <summary>
    ///     Gets or sets the Microsoft Entra tenant ID selected during Azure OAuth.
    /// </summary>
    public string? AzureTenantId { get; set; }

    /// <summary>
    ///     Gets or sets the Azure subscription ID selected for cloud deployments.
    /// </summary>
    public string? AzureSubscriptionId { get; set; }

    /// <summary>
    ///     Gets or sets the Azure OAuth access token for delegated Azure API calls.
    /// </summary>
    public string? AzureAccessToken { get; set; }

    /// <summary>
    ///     Gets or sets the Azure OAuth refresh token used to request scoped Azure API tokens.
    /// </summary>
    public string? AzureRefreshToken { get; set; }

    /// <summary>
    ///     Gets or sets the UTC timestamp when the current Azure access token expires.
    /// </summary>
    public DateTimeOffset? AzureTokenExpiresAt { get; set; }
}