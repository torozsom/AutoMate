namespace Services.Azure;

/// <summary>
///     Shared Azure Resource Manager constants used by the Azure integration services.
/// </summary>
internal static class AzureConstants
{
    /// <summary>
    ///     Base Azure Resource Manager endpoint used for direct REST calls.
    /// </summary>
    public const string ManagementEndpoint = "https://management.azure.com";

    /// <summary>
    ///     GitHub Actions OIDC issuer URL trusted by Azure federated credentials.
    /// </summary>
    public const string GitHubTokenIssuer = "https://token.actions.githubusercontent.com";

    /// <summary>
    ///     Audience Azure expects when GitHub exchanges an OIDC token for an Azure token.
    /// </summary>
    public const string AzureTokenExchangeAudience = "api://AzureADTokenExchange";

    /// <summary>
    ///     API version used for role assignment management requests.
    /// </summary>
    public const string RoleAssignmentApiVersion = "2022-04-01";

    /// <summary>
    ///     API version used for Azure resource provider registration requests.
    /// </summary>
    public const string ResourceProviderApiVersion = "2021-04-01";

    /// <summary>
    ///     API version used for managed identity federated credential requests.
    /// </summary>
    public const string ManagedIdentityFederatedCredentialApiVersion = "2024-11-30";

    /// <summary>
    ///     Built-in Azure Contributor role definition ID.
    /// </summary>
    public const string ContributorRoleDefinitionId = "b24988ac-6180-42a0-ab88-20f7382dd24c";

    /// <summary>
    ///     Principal type sent when assigning roles to user-assigned managed identities.
    /// </summary>
    public const string ContributorPrincipalType = "ServicePrincipal";

    /// <summary>
    ///     Registration state value Azure returns when a resource provider is ready.
    /// </summary>
    public const string RegisteredProviderState = "Registered";

    /// <summary>
    ///     Polling delay used while Azure propagates newly created infrastructure metadata.
    /// </summary>
    public static readonly TimeSpan PollDelay = TimeSpan.FromSeconds(5);
}