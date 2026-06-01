using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Azure;
using Azure.ResourceManager;
using Azure.ResourceManager.ManagedServiceIdentities;
using Azure.ResourceManager.Resources;
using Core.DTO;
using Microsoft.Extensions.Logging;

namespace Services.Azure;

/// <summary>
///     Uses Azure Resource Manager to prepare OIDC trust for GitHub Actions cloud deployments.
/// </summary>
public class AzureDeploymentOrchestrator(
    IHttpClientFactory httpClientFactory,
    ILogger<AzureDeploymentOrchestrator> logger) : IAzureDeploymentOrchestrator
{
    private const string GitHubTokenIssuer = "https://token.actions.githubusercontent.com";
    private const string AzureTokenExchangeAudience = "api://AzureADTokenExchange";
    private const string AzureManagementApiVersion = "2022-04-01";
    private const string AzureResourceProviderApiVersion = "2021-04-01";
    private const string ManagedIdentityFederatedCredentialApiVersion = "2024-11-30";
    private const string ContributorRoleDefinitionId = "b24988ac-6180-42a0-ab88-20f7382dd24c";
    private const int FederatedCredentialReadinessAttempts = 6;
    private const int ProviderRegistrationAttempts = 24;

    private static readonly string[] BaseRequiredResourceProviders =
    [
        "Microsoft.App",
        "Microsoft.OperationalInsights"
    ];


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

        var subject = CreateGitHubBranchSubject(repositoryOwner, repositoryName, branchName);
        var identityName = ToAzureIdentityName(
            $"automate-{config.CloudContainerAppName}-{repositoryName}-{CreateShortHash($"{resourceGroupName}:{repositoryOwner}/{repositoryName}")}");
        var federatedCredentialName = ToAzureIdentityName(
            $"github-{repositoryOwner}-{repositoryName}-{branchName}-{CreateShortHash(subject)}");

        var armClient = new ArmClient(new StaticAccessTokenCredential(credentials.AccessToken),
            credentials.SubscriptionId);
        var subscription = armClient.GetSubscriptionResource(SubscriptionResource.CreateResourceIdentifier(
            credentials.SubscriptionId));

        foreach (var providerNamespace in GetRequiredResourceProviders(config))
            await EnsureResourceProviderRegisteredAsync(credentials.SubscriptionId, providerNamespace,
                credentials.AccessToken, cancellationToken);

        var resourceGroup = await EnsureResourceGroupAsync(subscription, resourceGroupName, config.CloudAzureRegion,
            cancellationToken);

        var identity = await EnsureUserAssignedIdentityAsync(resourceGroup, identityName, config.CloudAzureRegion,
            cancellationToken);

        await EnsureFederatedCredentialAsync(identity, federatedCredentialName, subject, credentials.AccessToken,
            cancellationToken);
        await WaitForFederatedCredentialReadinessAsync(identity, federatedCredentialName, subject,
            credentials.AccessToken, cancellationToken);

        await EnsureContributorAssignmentAsync(resourceGroup, identity, credentials.AccessToken, cancellationToken);

        logger.LogInformation(
            "[AzureDeploymentOrchestrator] OIDC trust configured for {Owner}/{Repo}@{Branch}. Identity: {IdentityResourceId}. ClientId: {ClientId}. TenantId: {TenantId}. FederatedCredential: {FederatedCredentialName}. Issuer: {Issuer}. Subject: {Subject}. Audience: {Audience}.",
            repositoryOwner, repositoryName, branchName, identity.Id, identity.Data.ClientId, identity.Data.TenantId,
            federatedCredentialName, GitHubTokenIssuer, subject, AzureTokenExchangeAudience);

        return new AzureOidcSetupResultDto
        {
            ClientId = identity.Data.ClientId?.ToString() ?? string.Empty,
            TenantId = identity.Data.TenantId?.ToString() ?? credentials.TenantId,
            SubscriptionId = credentials.SubscriptionId,
            IdentityResourceId = identity.Id.ToString(),
            FederatedCredentialName = federatedCredentialName,
            Issuer = GitHubTokenIssuer,
            Subject = subject,
            Audience = AzureTokenExchangeAudience
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
    ///     Ensures the specified federated credential exists for the given user-assigned identity, creating it if necessary.
    /// </summary>
    /// <param name="identity">The user-assigned identity for which to ensure the federated credential exists.</param>
    /// <param name="credentialName">The name of the federated credential to ensure.</param>
    /// <param name="subject">The exact GitHub OIDC subject to trust.</param>
    /// <param name="cancellationToken">The cancellation token for the operation.</param>
    private async Task EnsureFederatedCredentialAsync(UserAssignedIdentityResource identity,
        string credentialName, string subject, string accessToken, CancellationToken cancellationToken)
    {
        using var request = CreateFederatedCredentialRequest(identity, credentialName, accessToken, HttpMethod.Put);
        request.Content = JsonContent.Create(new
        {
            properties = new
            {
                issuer = GitHubTokenIssuer,
                subject,
                audiences = new[] { AzureTokenExchangeAudience }
            }
        });

        using var response = await httpClientFactory.CreateClient().SendAsync(request, cancellationToken);
        if (response.IsSuccessStatusCode)
            return;

        var responseText = await response.Content.ReadAsStringAsync(cancellationToken);
        throw new InvalidOperationException(
            $"Azure federated credential '{credentialName}' could not be created or updated. Azure response: {responseText}");
    }


    /// <summary>
    ///     Reads the federated credential back from ARM until the exact issuer, subject, and audience are visible.
    /// </summary>
    /// <param name="identity">The managed identity that owns the federated credential.</param>
    /// <param name="credentialName">The federated credential name.</param>
    /// <param name="subject">The exact GitHub OIDC subject expected by the workflow.</param>
    /// <param name="accessToken">The access token for Azure Resource Manager.</param>
    /// <param name="cancellationToken">The cancellation token for the operation.</param>
    private async Task WaitForFederatedCredentialReadinessAsync(UserAssignedIdentityResource identity,
        string credentialName, string subject, string accessToken, CancellationToken cancellationToken)
    {
        string? lastIssuer = null;
        string? lastSubject = null;
        string? lastAudiences = null;

        for (var attempt = 1; attempt <= FederatedCredentialReadinessAttempts; attempt++)
        {
            try
            {
                var credential = await GetFederatedCredentialPropertiesAsync(identity, credentialName, accessToken,
                    cancellationToken);
                lastIssuer = credential.Issuer;
                lastSubject = credential.Subject;
                lastAudiences = string.Join(", ", credential.Audiences);

                if (string.Equals(credential.Issuer, GitHubTokenIssuer, StringComparison.Ordinal) &&
                    string.Equals(credential.Subject, subject, StringComparison.Ordinal) &&
                    credential.Audiences.Contains(AzureTokenExchangeAudience, StringComparer.Ordinal))
                    return;
            }
            catch (HttpRequestException ex)
            {
                logger.LogDebug(ex,
                    "[AzureDeploymentOrchestrator] Federated credential {CredentialName} was not readable on attempt {Attempt}.",
                    credentialName, attempt);
            }
            catch (JsonException ex)
            {
                logger.LogDebug(ex,
                    "[AzureDeploymentOrchestrator] Federated credential {CredentialName} was not readable on attempt {Attempt}.",
                    credentialName, attempt);
            }

            if (attempt < FederatedCredentialReadinessAttempts)
                await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken);
        }

        logger.LogWarning(
            "[AzureDeploymentOrchestrator] Federated credential {CredentialName} was created, but ARM did not read back the expected OIDC issuer/subject/audience before the local readiness timeout. Continuing because Azure may still be propagating the credential. Expected issuer: {ExpectedIssuer}. Actual issuer: {ActualIssuer}. Expected subject: {ExpectedSubject}. Actual subject: {ActualSubject}. Expected audience: {ExpectedAudience}. Actual audiences: {ActualAudiences}.",
            credentialName, GitHubTokenIssuer, lastIssuer ?? "<unavailable>", subject, lastSubject ?? "<unavailable>",
            AzureTokenExchangeAudience, lastAudiences ?? "<unavailable>");
    }


    /// <summary>
    ///     Reads federated credential properties through ARM REST without URI-normalizing the issuer.
    /// </summary>
    /// <param name="identity">The managed identity that owns the federated credential.</param>
    /// <param name="credentialName">The federated credential name.</param>
    /// <param name="accessToken">The access token for Azure Resource Manager.</param>
    /// <param name="cancellationToken">The cancellation token for the operation.</param>
    /// <returns>The federated credential properties.</returns>
    private async Task<FederatedCredentialProperties> GetFederatedCredentialPropertiesAsync(
        UserAssignedIdentityResource identity, string credentialName, string accessToken,
        CancellationToken cancellationToken)
    {
        using var request = CreateFederatedCredentialRequest(identity, credentialName, accessToken, HttpMethod.Get);
        using var response = await httpClientFactory.CreateClient().SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var payload = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        var properties = payload.RootElement.GetProperty("properties");
        var audiences = properties.GetProperty("audiences")
            .EnumerateArray()
            .Select(audience => audience.GetString())
            .Where(audience => !string.IsNullOrWhiteSpace(audience))
            .Cast<string>()
            .ToArray();

        return new FederatedCredentialProperties(
            properties.GetProperty("issuer").GetString() ?? string.Empty,
            properties.GetProperty("subject").GetString() ?? string.Empty,
            audiences);
    }


    /// <summary>
    ///     Builds an Azure Resource Manager request for a managed identity federated credential.
    /// </summary>
    /// <param name="identity">The managed identity that owns the federated credential.</param>
    /// <param name="credentialName">The federated credential name.</param>
    /// <param name="accessToken">The access token for Azure Resource Manager.</param>
    /// <param name="method">The HTTP method to use.</param>
    /// <returns>The configured HTTP request.</returns>
    private static HttpRequestMessage CreateFederatedCredentialRequest(UserAssignedIdentityResource identity,
        string credentialName, string accessToken, HttpMethod method)
    {
        var requestUri =
            $"https://management.azure.com{identity.Id}/federatedIdentityCredentials/{Uri.EscapeDataString(credentialName)}?api-version={ManagedIdentityFederatedCredentialApiVersion}";

        var request = new HttpRequestMessage(method, requestUri);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        return request;
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
                roleDefinitionId =
                    $"{scope}/providers/Microsoft.Authorization/roleDefinitions/{ContributorRoleDefinitionId}",
                principalId,
                principalType = "ServicePrincipal"
            }
        });

        var httpClient = httpClientFactory.CreateClient();
        using var response = await httpClient.SendAsync(request, cancellationToken);
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
    ///     Ensures an Azure resource provider namespace required by generated Bicep templates is registered.
    /// </summary>
    /// <param name="subscriptionId">The target Azure subscription ID.</param>
    /// <param name="providerNamespace">The resource provider namespace.</param>
    /// <param name="accessToken">The access token for Azure Resource Manager.</param>
    /// <param name="cancellationToken">The cancellation token for the operation.</param>
    private async Task EnsureResourceProviderRegisteredAsync(string subscriptionId, string providerNamespace,
        string accessToken, CancellationToken cancellationToken)
    {
        var registrationState = await GetResourceProviderRegistrationStateAsync(subscriptionId, providerNamespace,
            accessToken, cancellationToken);

        if (string.Equals(registrationState, "Registered", StringComparison.OrdinalIgnoreCase))
            return;

        using (var request = CreateResourceProviderRequest(subscriptionId, providerNamespace, accessToken,
                   HttpMethod.Post, "/register"))
        using (var response = await httpClientFactory.CreateClient().SendAsync(request, cancellationToken))
        {
            if (!response.IsSuccessStatusCode)
            {
                var responseText = await response.Content.ReadAsStringAsync(cancellationToken);
                if (response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized)
                    throw new InvalidOperationException(
                        $"AutoMate cannot register Azure resource provider '{providerNamespace}' on subscription '{subscriptionId}'. " +
                        "Register it manually in Azure Portal, or connect with an Azure account that can register resource providers at subscription scope. Azure response: " +
                        responseText);

                response.EnsureSuccessStatusCode();
            }
        }

        for (var attempt = 1; attempt <= ProviderRegistrationAttempts; attempt++)
        {
            registrationState = await GetResourceProviderRegistrationStateAsync(subscriptionId, providerNamespace,
                accessToken, cancellationToken);

            if (string.Equals(registrationState, "Registered", StringComparison.OrdinalIgnoreCase))
            {
                logger.LogInformation(
                    "[AzureDeploymentOrchestrator] Azure resource provider {ProviderNamespace} is registered on subscription {SubscriptionId}.",
                    providerNamespace, subscriptionId);
                return;
            }

            if (attempt < ProviderRegistrationAttempts)
                await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken);
        }

        throw new InvalidOperationException(
            $"Azure resource provider '{providerNamespace}' registration did not complete before the timeout. Current state: {registrationState ?? "<unknown>"}.");
    }


    /// <summary>
    ///     Returns the Azure resource providers required by Container Apps plus the selected managed database services.
    /// </summary>
    /// <param name="config">The active deployment configuration.</param>
    /// <returns>The provider namespaces that must be registered before deployment.</returns>
    private static IReadOnlyCollection<string> GetRequiredResourceProviders(DeploymentConfigDto config)
    {
        var providers = new HashSet<string>(BaseRequiredResourceProviders, StringComparer.OrdinalIgnoreCase);

        foreach (var database in config.Databases)
            switch (database.DbType.Trim().ToLowerInvariant())
            {
                case "postgresql":
                case "postgres":
                    providers.Add("Microsoft.DBforPostgreSQL");
                    break;
                case "mysql":
                    providers.Add("Microsoft.DBforMySQL");
                    break;
                case "sqlserver":
                case "sql-server":
                case "mssql":
                case "microsoft sql server":
                    providers.Add("Microsoft.Sql");
                    break;
                case "mongodb":
                case "mongo":
                    providers.Add("Microsoft.DocumentDB");
                    break;
                case "redis":
                    providers.Add("Microsoft.Cache");
                    break;
            }

        return providers;
    }


    /// <summary>
    ///     Reads the registration state for an Azure resource provider namespace.
    /// </summary>
    /// <param name="subscriptionId">The target Azure subscription ID.</param>
    /// <param name="providerNamespace">The resource provider namespace.</param>
    /// <param name="accessToken">The access token for Azure Resource Manager.</param>
    /// <param name="cancellationToken">The cancellation token for the operation.</param>
    /// <returns>The registration state reported by Azure Resource Manager.</returns>
    private async Task<string?> GetResourceProviderRegistrationStateAsync(string subscriptionId,
        string providerNamespace, string accessToken, CancellationToken cancellationToken)
    {
        using var request = CreateResourceProviderRequest(subscriptionId, providerNamespace, accessToken,
            HttpMethod.Get);
        using var response = await httpClientFactory.CreateClient().SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var payload = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);

        return payload.RootElement.TryGetProperty("registrationState", out var registrationState)
            ? registrationState.GetString()
            : null;
    }


    /// <summary>
    ///     Creates an Azure Resource Manager request for subscription resource provider registration.
    /// </summary>
    /// <param name="subscriptionId">The target Azure subscription ID.</param>
    /// <param name="providerNamespace">The resource provider namespace.</param>
    /// <param name="accessToken">The access token for Azure Resource Manager.</param>
    /// <param name="method">The HTTP method to use.</param>
    /// <param name="suffix">An optional provider operation suffix.</param>
    /// <returns>The configured HTTP request.</returns>
    private static HttpRequestMessage CreateResourceProviderRequest(string subscriptionId, string providerNamespace,
        string accessToken, HttpMethod method, string suffix = "")
    {
        var requestUri =
            $"https://management.azure.com/subscriptions/{Uri.EscapeDataString(subscriptionId)}/providers/{Uri.EscapeDataString(providerNamespace)}{suffix}?api-version={AzureResourceProviderApiVersion}";

        var request = new HttpRequestMessage(method, requestUri);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        return request;
    }


    /// <summary>
    ///     Creates a deterministic GUID based on the input string using SHA-256 hashing.
    /// </summary>
    /// <param name="value">The input string for which to create a deterministic GUID.</param>
    /// <returns>The deterministic GUID.</returns>
    private static Guid CreateDeterministicGuid(string value)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return new Guid(bytes[..16]);
    }


    /// <summary>
    ///     Creates the exact GitHub branch subject used by GitHub Actions OIDC tokens.
    /// </summary>
    /// <param name="repositoryOwner">The GitHub repository owner.</param>
    /// <param name="repositoryName">The GitHub repository name.</param>
    /// <param name="branchName">The branch that runs the workflow.</param>
    /// <returns>The GitHub OIDC subject claim.</returns>
    private static string CreateGitHubBranchSubject(string repositoryOwner, string repositoryName, string branchName)
    {
        return $"repo:{repositoryOwner}/{repositoryName}:ref:refs/heads/{branchName}";
    }


    /// <summary>
    ///     Creates a short deterministic lowercase hash for Azure resource names.
    /// </summary>
    /// <param name="value">The value to hash.</param>
    /// <returns>A short lowercase hexadecimal hash.</returns>
    private static string CreateShortHash(string value)
    {
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)))[..10].ToLowerInvariant();
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

    private sealed record FederatedCredentialProperties(string Issuer, string Subject, IReadOnlyList<string> Audiences);
}
