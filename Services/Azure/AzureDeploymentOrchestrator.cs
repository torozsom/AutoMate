using Azure;
using Azure.ResourceManager;
using Azure.ResourceManager.ManagedServiceIdentities;
using Azure.ResourceManager.ManagedServiceIdentities.Models;
using Azure.ResourceManager.Resources;
using Core.DTO;
using Microsoft.Extensions.Logging;
using System.Net.Http.Headers;
using System.Net.Http.Json;

namespace Services.Azure;

/// <summary>
///     Uses Azure Resource Manager to prepare OIDC trust for GitHub Actions cloud deployments.
/// </summary>
public class AzureDeploymentOrchestrator(
    IHttpClientFactory httpClientFactory,
    ILogger<AzureDeploymentOrchestrator> logger) : IAzureDeploymentOrchestrator
{
    private const string GitHubTokenIssuer = "https://token.actions.githubusercontent.com";
    private const string AzureManagementApiVersion = "2022-04-01";
    private const string ContributorRoleDefinitionId = "b24988ac-6180-42a0-ab88-20f7382dd24c";


    /// <inheritdoc />
    public async Task<AzureOidcSetupResultDto> EnsureFederatedIdentityAsync(AzureCloudCredentialsDto credentials,
        DeploymentConfigDto config, string repositoryOwner, string repositoryName, string branchName,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(credentials);
        ArgumentNullException.ThrowIfNull(config);

        if (string.IsNullOrWhiteSpace(credentials.AccessToken))
            throw new ArgumentException("Azure access token is required for cloud deployment setup.",
                nameof(credentials));

        if (string.IsNullOrWhiteSpace(credentials.SubscriptionId))
            throw new ArgumentException("Azure subscription ID is required for cloud deployment setup.",
                nameof(credentials));

        if (string.IsNullOrWhiteSpace(credentials.TenantId))
            throw new ArgumentException("Azure tenant ID is required for cloud deployment setup.", nameof(credentials));

        if (string.IsNullOrWhiteSpace(repositoryOwner))
            throw new ArgumentException("Repository owner is required for cloud deployment setup.",
                nameof(repositoryOwner));

        if (string.IsNullOrWhiteSpace(repositoryName))
            throw new ArgumentException("Repository name is required for cloud deployment setup.",
                nameof(repositoryName));

        if (string.IsNullOrWhiteSpace(branchName))
            throw new ArgumentException("Branch name is required for cloud deployment setup.", nameof(branchName));

        var resourceGroupName = config.CloudResourceGroupName;
        if (string.IsNullOrWhiteSpace(resourceGroupName))
            throw new ArgumentException("Azure resource group name is required for cloud deployment setup.",
                nameof(config));

        var identityName = ToAzureIdentityName($"automate-{config.CloudContainerAppName}-{repositoryName}");
        var federatedCredentialName = ToAzureIdentityName($"github-{branchName}");

        var armClient = new ArmClient(new StaticAccessTokenCredential(credentials.AccessToken),
            credentials.SubscriptionId);
        var subscription = armClient.GetSubscriptionResource(SubscriptionResource.CreateResourceIdentifier(
            credentials.SubscriptionId));

        var resourceGroup = await EnsureResourceGroupAsync(subscription, resourceGroupName, config.CloudAzureRegion,
            cancellationToken);

        var identity = await EnsureUserAssignedIdentityAsync(resourceGroup, identityName, config.CloudAzureRegion,
            cancellationToken);

        await EnsureFederatedCredentialAsync(identity, federatedCredentialName, repositoryOwner, repositoryName,
            branchName, cancellationToken);

        await EnsureContributorAssignmentAsync(resourceGroup, identity, credentials.AccessToken, cancellationToken);

        logger.LogInformation(
            "[AzureDeploymentOrchestrator] OIDC trust configured for {Owner}/{Repo}@{Branch} using identity {IdentityName} ({ClientId}) in tenant {TenantId}.",
            repositoryOwner, repositoryName, branchName, identityName, identity.Data.ClientId, identity.Data.TenantId);

        return new AzureOidcSetupResultDto
        {
            ClientId = identity.Data.ClientId?.ToString() ?? string.Empty,
            TenantId = identity.Data.TenantId?.ToString() ?? credentials.TenantId,
            SubscriptionId = credentials.SubscriptionId
        };
    }


    /// <summary>
    ///     Ensures the specified resource group exists, creating it if necessary.
    /// </summary>
    /// <param name="subscription">The Azure subscription resource.</param>
    /// <param name="resourceGroupName">The name of the resource group to ensure.</param>
    /// <param name="location">The Azure region for the resource group.</param>
    /// <param name="cancellationToken">The cancellation token for the operation.</param>
    /// <returns>The existing or newly created resource group resource.</returns>
    private static async Task<ResourceGroupResource> EnsureResourceGroupAsync(SubscriptionResource subscription,
        string resourceGroupName, string location, CancellationToken cancellationToken)
    {
        var collection = subscription.GetResourceGroups();
        var data = new ResourceGroupData(location);
        var result = await collection.CreateOrUpdateAsync(WaitUntil.Completed, resourceGroupName, data,
            cancellationToken);
        return result.Value;
    }


    /// <summary>
    ///     Ensures the specified user-assigned identity exists, creating it if necessary.
    /// </summary>
    /// <param name="resourceGroup">The resource group to which the identity belongs.</param>
    /// <param name="identityName">The name of the identity to ensure.</param>
    /// <param name="location">The Azure region for the identity.</param>
    /// <param name="cancellationToken">The cancellation token for the operation.</param>
    /// <returns>The existing or newly created user-assigned identity resource.</returns>
    private static async Task<UserAssignedIdentityResource> EnsureUserAssignedIdentityAsync(
        ResourceGroupResource resourceGroup, string identityName, string location, CancellationToken cancellationToken)
    {
        var collection = resourceGroup.GetUserAssignedIdentities();
        var data = new UserAssignedIdentityData(location);
        var result = await collection.CreateOrUpdateAsync(WaitUntil.Completed, identityName, data, cancellationToken);
        return result.Value;
    }


    /// <summary>
    ///    Ensures the specified federated credential exists for the given user-assigned identity, creating it if necessary.
    /// </summary>
    /// <param name="identity">The user-assigned identity for which to ensure the federated credential exists.</param>
    /// <param name="credentialName">The name of the federated credential to ensure.</param>
    /// <param name="repositoryOwner">The owner of the Git repository.</param>
    /// <param name="repositoryName">The name of the Git repository.</param>
    /// <param name="branchName">The name of the branch.</param>
    /// <param name="cancellationToken">The cancellation token for the operation.</param>
    private static async Task EnsureFederatedCredentialAsync(UserAssignedIdentityResource identity,
        string credentialName, string repositoryOwner, string repositoryName, string branchName,
        CancellationToken cancellationToken)
    {
        var collection = identity.GetFederatedIdentityCredentials();
        var subject = $"repo:{repositoryOwner}/{repositoryName}:ref:refs/heads/{branchName}";
        var data = new FederatedIdentityCredentialData
        {
            Issuer = GitHubTokenIssuer,
            Subject = subject
        };
        data.Audiences.Add("api://AzureADTokenExchange");

        await collection.CreateOrUpdateAsync(WaitUntil.Completed, credentialName, data, cancellationToken);
    }


    /// <summary>
    ///     Ensures the specified user-assigned identity has the Contributor role assignment
    ///     on the resource group, creating it if necessary.
    /// </summary>
    /// <param name="resourceGroup">The resource group on which to assign the role.</param>
    /// <param name="identity">The user-assigned identity for which to assign the role.</param>
    /// <param name="accessToken">The access token for the Azure API request.</param>
    /// <param name="cancellationToken">The cancellation token for the operation.</param>
    /// <exception cref="InvalidOperationException">Thrown if the principal ID of the identity cannot be determined.</exception>
    private async Task EnsureContributorAssignmentAsync(ResourceGroupResource resourceGroup,
        UserAssignedIdentityResource identity, string accessToken, CancellationToken cancellationToken)
    {
        var principalId = identity.Data.PrincipalId?.ToString();
        if (string.IsNullOrWhiteSpace(principalId))
            throw new InvalidOperationException("Azure managed identity principal ID could not be loaded.");

        var scope = resourceGroup.Id.ToString();
        var assignmentName = CreateDeterministicGuid($"{scope}:{principalId}:{ContributorRoleDefinitionId}");
        var requestUri =
            $"https://management.azure.com{scope}/providers/Microsoft.Authorization/roleAssignments/{assignmentName}?api-version={AzureManagementApiVersion}";

        using var request = new HttpRequestMessage(HttpMethod.Put, requestUri);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        request.Content = JsonContent.Create(new
        {
            properties = new
            {
                roleDefinitionId = $"{scope}/providers/Microsoft.Authorization/roleDefinitions/{ContributorRoleDefinitionId}",
                principalId,
                principalType = "ServicePrincipal"
            }
        });

        var httpClient = httpClientFactory.CreateClient();
        using var response = await httpClient.SendAsync(request, cancellationToken);
        if (response.IsSuccessStatusCode)
            return;

        var responseText = await response.Content.ReadAsStringAsync(cancellationToken);
        if (response.StatusCode is System.Net.HttpStatusCode.Forbidden or System.Net.HttpStatusCode.Unauthorized)
            throw new InvalidOperationException(
                "AutoMate created the Azure managed identity, but the connected Azure account cannot assign the Contributor role to it. " +
                "Connect with an account that has Owner or User Access Administrator rights on the resource group/subscription, " +
                "or manually assign Contributor to the managed identity before redeploying. Azure response: " + responseText);

        response.EnsureSuccessStatusCode();
    }


    /// <summary>
    ///    Creates a deterministic GUID based on the input string using SHA-256 hashing.
    /// </summary>
    /// <param name="value">The input string for which to create a deterministic GUID.</param>
    /// <returns>The deterministic GUID.</returns>
    private static Guid CreateDeterministicGuid(string value)
    {
        var bytes = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(value));
        return new Guid(bytes[..16]);
    }


    /// <summary>
    ///     Normalizes the input string to create a valid Azure resource name by converting to lowercase,
    ///     replacing invalid characters with hyphens, and trimming to a maximum length of 80 characters.
    /// </summary>
    /// <param name="value">The input string to normalize.</param>
    /// <returns>The normalized Azure resource name.</returns>
    private static string ToAzureIdentityName(string value)
    {
        var normalized = new string(value
            .Trim()
            .ToLowerInvariant()
            .Select(c => char.IsLetterOrDigit(c) ? c : '-')
            .ToArray());

        normalized = string.Join('-', normalized.Split('-',
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

        if (string.IsNullOrWhiteSpace(normalized))
            normalized = "automate-identity";

        return normalized.Length <= 80 ? normalized : normalized[..80].TrimEnd('-');
    }
}
