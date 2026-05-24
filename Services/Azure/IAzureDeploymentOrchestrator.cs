using Core.DTO;

namespace Services.Azure;

/// <summary>
///     Configures Azure resources required for GitHub Actions OIDC cloud deployments.
/// </summary>
public interface IAzureDeploymentOrchestrator
{
    /// <summary>
    ///     Ensures a user-assigned managed identity and federated credential exist for the GitHub repository branch.
    /// </summary>
    /// <param name="credentials">The connected user's Azure credentials.</param>
    /// <param name="config">The cloud deployment configuration.</param>
    /// <param name="repositoryOwner">The GitHub repository owner.</param>
    /// <param name="repositoryName">The GitHub repository name.</param>
    /// <param name="branchName">The branch that will run the workflow.</param>
    /// <param name="cancellationToken">Propagates notification that operations should be canceled.</param>
    /// <returns>OIDC login values to store as GitHub repository secrets.</returns>
    Task<AzureOidcSetupResultDto> EnsureFederatedIdentityAsync(AzureCloudCredentialsDto credentials,
        DeploymentConfigDto config, string repositoryOwner, string repositoryName, string branchName,
        CancellationToken cancellationToken = default);
}
