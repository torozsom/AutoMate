using Azure;
using Azure.ResourceManager;
using Azure.ResourceManager.ManagedServiceIdentities;
using Azure.ResourceManager.ManagedServiceIdentities.Models;
using Azure.ResourceManager.Resources;
using Core.DTO;
using Microsoft.Extensions.Logging;

namespace Services.Azure;

/// <summary>
///     Uses Azure Resource Manager to prepare OIDC trust for GitHub Actions cloud deployments.
/// </summary>
public class AzureDeploymentOrchestrator(ILogger<AzureDeploymentOrchestrator> logger) : IAzureDeploymentOrchestrator
{
    private const string GitHubTokenIssuer = "https://token.actions.githubusercontent.com";

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

        logger.LogInformation(
            "[AzureDeploymentOrchestrator] OIDC trust configured for {Owner}/{Repo}@{Branch} using identity {IdentityName}.",
            repositoryOwner, repositoryName, branchName, identityName);

        return new AzureOidcSetupResultDto
        {
            ClientId = identity.Data.ClientId?.ToString() ?? string.Empty,
            TenantId = credentials.TenantId,
            SubscriptionId = credentials.SubscriptionId
        };
    }

    private static async Task<ResourceGroupResource> EnsureResourceGroupAsync(SubscriptionResource subscription,
        string resourceGroupName, string location, CancellationToken cancellationToken)
    {
        var collection = subscription.GetResourceGroups();
        var data = new ResourceGroupData(location);
        var result = await collection.CreateOrUpdateAsync(WaitUntil.Completed, resourceGroupName, data,
            cancellationToken);
        return result.Value;
    }

    private static async Task<UserAssignedIdentityResource> EnsureUserAssignedIdentityAsync(
        ResourceGroupResource resourceGroup, string identityName, string location, CancellationToken cancellationToken)
    {
        var collection = resourceGroup.GetUserAssignedIdentities();
        var data = new UserAssignedIdentityData(location);
        var result = await collection.CreateOrUpdateAsync(WaitUntil.Completed, identityName, data, cancellationToken);
        return result.Value;
    }

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
