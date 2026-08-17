using Azure;
using Azure.ResourceManager;
using Azure.ResourceManager.ManagedServiceIdentities;
using Azure.ResourceManager.Resources;
using Core.DTO;

namespace Services.Azure;

/// <summary>
///     Provisions Azure resource groups and user-assigned managed identities through the Azure SDK.
/// </summary>
internal static class AzureManagedIdentityProvisioner
{
    /// <summary>
    ///     Creates an Azure SDK subscription resource using the connected user's access token.
    /// </summary>
    public static SubscriptionResource CreateSubscriptionResource(AzureCloudCredentialsDto credentials)
    {
        var armClient = new ArmClient(new StaticAccessTokenCredential(credentials.AccessToken),
            credentials.SubscriptionId);

        return armClient.GetSubscriptionResource(SubscriptionResource.CreateResourceIdentifier(
            credentials.SubscriptionId));
    }

    /// <summary>
    ///     Creates or updates the resource group that will contain cloud deployment resources.
    /// </summary>
    public static async Task<ResourceGroupResource> EnsureResourceGroupAsync(SubscriptionResource subscription,
        string resourceGroupName, string location, CancellationToken cancellationToken)
    {
        var collection = subscription.GetResourceGroups();
        var data = new ResourceGroupData(location);
        var result = await collection.CreateOrUpdateAsync(WaitUntil.Completed, resourceGroupName, data,
            cancellationToken);
        return result.Value;
    }

    /// <summary>
    ///     Creates or updates the managed identity that GitHub Actions uses for Azure deployment.
    /// </summary>
    public static async Task<UserAssignedIdentityResource> EnsureUserAssignedIdentityAsync(
        ResourceGroupResource resourceGroup, string identityName, string location, CancellationToken cancellationToken)
    {
        var collection = resourceGroup.GetUserAssignedIdentities();
        var data = new UserAssignedIdentityData(location);
        var result = await collection.CreateOrUpdateAsync(WaitUntil.Completed, identityName, data, cancellationToken);
        return result.Value;
    }
}