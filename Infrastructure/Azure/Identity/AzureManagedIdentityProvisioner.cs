using Azure;
using Azure.ResourceManager;
using Azure.ResourceManager.ManagedServiceIdentities;
using Azure.ResourceManager.Resources;
using Domain.DTO;

namespace Infrastructure.Azure;

/// <summary>
///     Provisions Azure resource groups and user-assigned managed identities through the Azure
///     SDK.
/// </summary>
internal static class AzureManagedIdentityProvisioner
{
    /// <summary>
    ///     Creates an Azure SDK subscription resource using the connected user's access token.
    /// </summary>
    /// <param name="credentials">The connected Azure credentials used for resource preparation.</param>
    /// <returns>The subscription resource projection derived from the verified inputs.</returns>
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
    /// <param name="subscription">Verified Azure subscription resource.</param>
    /// <param name="resourceGroupName">Azure resource-group name used by this deployment.</param>
    /// <param name="location">Recorded resource location or relative artifact path.</param>
    /// <param name="cancellationToken">Cancels the underlying operation when observed.</param>
    /// <returns>
    ///     A task returning the resource group resource projection derived from the verified
    ///     inputs.
    /// </returns>
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
    /// <param name="resourceGroup">Verified Azure resource group used for provisioning.</param>
    /// <param name="identityName">Azure managed-identity resource name.</param>
    /// <param name="location">Recorded resource location or relative artifact path.</param>
    /// <param name="cancellationToken">Cancels the underlying operation when observed.</param>
    /// <returns>
    ///     A task returning the user assigned identity resource projection derived from the
    ///     verified inputs.
    /// </returns>
    public static async Task<UserAssignedIdentityResource> EnsureUserAssignedIdentityAsync(
        ResourceGroupResource resourceGroup, string identityName, string location, CancellationToken cancellationToken)
    {
        var collection = resourceGroup.GetUserAssignedIdentities();
        var data = new UserAssignedIdentityData(location);
        var result = await collection.CreateOrUpdateAsync(WaitUntil.Completed, identityName, data, cancellationToken);
        return result.Value;
    }
}
