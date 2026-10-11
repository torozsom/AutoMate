using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using Azure.ResourceManager.ManagedServiceIdentities;
using Azure.ResourceManager.Resources;

namespace Infrastructure.Azure;

/// <summary>
///     Assigns Azure RBAC permissions needed by the deployment managed identity.
/// </summary>
internal sealed class AzureRoleAssignmentService(IHttpClientFactory httpClientFactory)
{
    /// <summary>Azure built-in AcrPull role.</summary>
    private const string AcrPullRole = "7f951dda-4ed3-4680-a7ca-43fe172d538d";

    /// <summary>Azure built-in AcrPush role.</summary>
    private const string AcrPushRole = "8311e382-0749-4cb8-b61a-304f252e45ec";

    /// <summary>Grants the workflow identity push and the app identity pull on one ACR.</summary>
    public async Task EnsureRegistryAssignmentsAsync(string registryId,
        UserAssignedIdentityResource workflowIdentity, UserAssignedIdentityResource pullIdentity,
        string accessToken, CancellationToken cancellationToken)
    {
        await EnsureRoleAsync(registryId, workflowIdentity, AcrPushRole, accessToken, cancellationToken);
        await EnsureRoleAsync(registryId, pullIdentity, AcrPullRole, accessToken, cancellationToken);
    }

    /// <summary>Creates a deterministic resource-scoped role assignment.</summary>
    private async Task EnsureRoleAsync(string scope, UserAssignedIdentityResource identity,
        string roleId, string accessToken, CancellationToken cancellationToken)
    {
        var principalId = identity.Data.PrincipalId?.ToString()
                          ?? throw new InvalidOperationException("Azure managed identity principal ID is missing.");
        var assignmentName = CreateDeterministicGuid($"{scope}:{principalId}:{roleId}");
        var requestUri = $"{AzureConstants.ManagementEndpoint}{scope}/providers/Microsoft.Authorization/" +
                         $"roleAssignments/{assignmentName}?api-version={AzureConstants.RoleAssignmentApiVersion}";
        var subscriptionScope = scope[..scope.IndexOf("/resourceGroups/", StringComparison.OrdinalIgnoreCase)];
        using var request = new HttpRequestMessage(HttpMethod.Put, requestUri);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        request.Content = JsonContent.Create(new
        {
            properties = new
            {
                roleDefinitionId = $"{subscriptionScope}/providers/Microsoft.Authorization/roleDefinitions/{roleId}",
                principalId,
                principalType = AzureConstants.ContributorPrincipalType
            }
        });
        using var response = await httpClientFactory.CreateClient().SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException(
                "AutoMate could not grant Azure Container Registry access. The connected Azure account needs role assignment rights.");
    }

    /// <summary>
    ///     Ensures the managed identity has Contributor access on the target resource group.
    /// </summary>
    public async Task EnsureContributorAssignmentAsync(ResourceGroupResource resourceGroup,
        UserAssignedIdentityResource identity, string accessToken, CancellationToken cancellationToken)
    {
        var principalId = identity.Data.PrincipalId?.ToString();
        if (string.IsNullOrWhiteSpace(principalId))
            throw new InvalidOperationException("Azure managed identity principal ID could not be loaded.");

        var scope = resourceGroup.Id.ToString();
        var assignmentName = CreateDeterministicGuid(
            $"{scope}:{principalId}:{AzureConstants.ContributorRoleDefinitionId}");
        var requestUri =
            $"{AzureConstants.ManagementEndpoint}{scope}/providers/Microsoft.Authorization/roleAssignments/{assignmentName}?api-version={AzureConstants.RoleAssignmentApiVersion}";

        using var request = new HttpRequestMessage(HttpMethod.Put, requestUri);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        request.Content = JsonContent.Create(new
        {
            properties = new
            {
                roleDefinitionId =
                    $"{scope}/providers/Microsoft.Authorization/roleDefinitions/{AzureConstants.ContributorRoleDefinitionId}",
                principalId,
                principalType = AzureConstants.ContributorPrincipalType
            }
        });

        using var response = await httpClientFactory.CreateClient().SendAsync(request, cancellationToken);
        if (response.IsSuccessStatusCode)
            return;

        var responseText = await response.Content.ReadAsStringAsync(cancellationToken);
        if (response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized)
            throw new InvalidOperationException(
                "AutoMate created the Azure managed identity, but the connected Azure account cannot assign the Contributor role to it. " +
                "Connect with an account that has Owner or User Access Administrator rights on the resource group/subscription, " +
                "or manually assign Contributor to the managed identity before redeploying. Azure response: " +
                responseText);

        response.EnsureSuccessStatusCode();
    }

    /// <summary>
    ///     Creates a stable GUID so repeated role assignment calls target the same ARM resource.
    /// </summary>
    private static Guid CreateDeterministicGuid(string value)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return new Guid(bytes[..16]);
    }
}