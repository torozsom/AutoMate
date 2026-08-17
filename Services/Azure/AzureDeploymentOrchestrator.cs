using Core.DTO;
using Microsoft.Extensions.Logging;

namespace Services.Azure;

/// <summary>
///     Uses Azure Resource Manager to prepare OIDC trust for GitHub Actions cloud deployments.
/// </summary>
public sealed class AzureDeploymentOrchestrator(
    IHttpClientFactory httpClientFactory,
    ILogger<AzureDeploymentOrchestrator> logger) : IAzureDeploymentOrchestrator
{
    /// <summary>
    ///     Handles direct ARM calls for managed identity federated credentials.
    /// </summary>
    private readonly AzureFederatedCredentialService _federatedCredentialService =
        new(httpClientFactory, logger);

    /// <summary>
    ///     Ensures Azure resource providers needed by the deployment configuration are registered.
    /// </summary>
    private readonly AzureResourceProviderRegistrar _resourceProviderRegistrar =
        new(httpClientFactory, logger);

    /// <summary>
    ///     Assigns resource-group permissions to the managed identity used by GitHub Actions.
    /// </summary>
    private readonly AzureRoleAssignmentService _roleAssignmentService = new(httpClientFactory);

    /// <inheritdoc />
    public async Task<AzureOidcSetupResultDto> EnsureFederatedIdentityAsync(AzureCloudCredentialsDto credentials,
        DeploymentConfigDto config, string repositoryOwner, string repositoryName, string branchName,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(credentials);
        ArgumentNullException.ThrowIfNull(config);

        var setupContext = AzureOidcSetupPlanner.Create(credentials, config, repositoryOwner, repositoryName,
            branchName);
        var subscription = AzureManagedIdentityProvisioner.CreateSubscriptionResource(credentials);

        await _resourceProviderRegistrar.EnsureRequiredProvidersAsync(credentials, config, cancellationToken);

        var resourceGroup = await AzureManagedIdentityProvisioner.EnsureResourceGroupAsync(subscription,
            setupContext.ResourceGroupName, config.CloudAzureRegion, cancellationToken);

        var identity = await AzureManagedIdentityProvisioner.EnsureUserAssignedIdentityAsync(resourceGroup,
            setupContext.IdentityName, config.CloudAzureRegion, cancellationToken);

        await _federatedCredentialService.EnsureAsync(identity, setupContext.FederatedCredentialName,
            setupContext.Subject, credentials.AccessToken, cancellationToken);

        await _roleAssignmentService.EnsureContributorAssignmentAsync(resourceGroup, identity,
            credentials.AccessToken, cancellationToken);

        logger.LogInformation(
            "[AzureDeploymentOrchestrator] OIDC trust configured for {Owner}/{Repo}@{Branch}. Identity: {IdentityResourceId}. ClientId: {ClientId}. TenantId: {TenantId}. FederatedCredential: {FederatedCredentialName}. Issuer: {Issuer}. Subject: {Subject}. Audience: {Audience}.",
            repositoryOwner, repositoryName, branchName, identity.Id, identity.Data.ClientId, identity.Data.TenantId,
            setupContext.FederatedCredentialName, AzureConstants.GitHubTokenIssuer, setupContext.Subject,
            AzureConstants.AzureTokenExchangeAudience);

        return AzureOidcSetupPlanner.CreateResult(credentials, identity, setupContext);
    }
}