using Application.Abstractions.Azure;
using Domain.DTO;
using Microsoft.Extensions.Logging;

namespace Infrastructure.Azure;

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

    /// <summary>Creates the customer's registry before GitHub Actions attempts its first image push.</summary>
    private readonly AzureContainerRegistryProvisioner _registryProvisioner = new(httpClientFactory);

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

        string? pullIdentityId = null;
        if (config.CloudRegistryName.EndsWith(".azurecr.io", StringComparison.OrdinalIgnoreCase))
        {
            var registryId = await _registryProvisioner.EnsureAsync(credentials, config, cancellationToken);
            var pullIdentity = await AzureManagedIdentityProvisioner.EnsureUserAssignedIdentityAsync(resourceGroup,
                setupContext.IdentityName + "-pull", config.CloudAzureRegion, cancellationToken);
            await _roleAssignmentService.EnsureRegistryAssignmentsAsync(registryId, identity, pullIdentity,
                credentials.AccessToken, cancellationToken);
            pullIdentityId = pullIdentity.Id.ToString();
        }

        logger.LogInformation("Azure OIDC trust configured.");

        return AzureOidcSetupPlanner.CreateResult(credentials, identity, setupContext) with
        {
            RegistryPullIdentityResourceId = pullIdentityId
        };
    }
}