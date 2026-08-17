using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using Azure.ResourceManager.ManagedServiceIdentities;
using Azure.ResourceManager.Resources;

namespace Services.Azure;

/// <summary>
///     Assigns Azure RBAC permissions needed by the deployment managed identity.
/// </summary>
internal sealed class AzureRoleAssignmentService(IHttpClientFactory httpClientFactory)
{
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