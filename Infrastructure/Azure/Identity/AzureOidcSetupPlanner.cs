using System.Security.Cryptography;
using System.Text;
using Azure.ResourceManager.ManagedServiceIdentities;
using Domain.DTO;

namespace Infrastructure.Azure;

/// <summary>
///     Builds deterministic Azure identity names and GitHub OIDC claims for cloud deployments.
/// </summary>
internal static class AzureOidcSetupPlanner
{
    /// <summary>
    ///     Creates the provisioning context for a GitHub repository branch deployment.
    /// </summary>
    /// <param name="credentials">The connected Azure credentials used for resource preparation.</param>
    /// <param name="config">The deployment configuration used by the operation.</param>
    /// <param name="repositoryOwner">The GitHub repository owner or organization.</param>
    /// <param name="repositoryName">The GitHub repository name.</param>
    /// <param name="branchName">The repository branch used for deployment assets or workflow selection.</param>
    /// <returns>Verified provisioning scope and repository/branch trust metadata.</returns>
    public static AzureOidcSetupContext Create(AzureCloudCredentialsDto credentials, DeploymentConfigDto config,
        string repositoryOwner, string repositoryName, string branchName)
    {
        ValidateSetupInput(credentials, config, repositoryOwner, repositoryName, branchName);

        var subject = CreateGitHubBranchSubject(repositoryOwner, repositoryName, branchName);
        var identityName = ToAzureIdentityName(
            $"automate-{config.CloudContainerAppName}-{repositoryName}-{CreateShortHash($"{config.CloudResourceGroupName}:{repositoryOwner}/{repositoryName}")}");
        var federatedCredentialName = ToAzureIdentityName(
            $"github-{repositoryOwner}-{repositoryName}-{branchName}-{CreateShortHash(subject)}");

        return new AzureOidcSetupContext(config.CloudResourceGroupName, subject, identityName,
            federatedCredentialName);
    }

    /// <summary>
    ///     Maps the created managed identity and setup context into the deployment DTO consumed by
    ///     callers.
    /// </summary>
    /// <param name="credentials">The connected Azure credentials used for resource preparation.</param>
    /// <param name="identity">Verified user-assigned Azure identity.</param>
    /// <param name="setupContext">
    ///     Verified Azure setup scope, repository identity and OIDC trust metadata.
    /// </param>
    /// <returns>The verified OIDC and managed-identity identifiers.</returns>
    public static AzureOidcSetupResultDto CreateResult(AzureCloudCredentialsDto credentials,
        UserAssignedIdentityResource identity, AzureOidcSetupContext setupContext)
    {
        return new AzureOidcSetupResultDto
        {
            ClientId = identity.Data.ClientId?.ToString() ?? string.Empty,
            TenantId = identity.Data.TenantId?.ToString() ?? credentials.TenantId,
            SubscriptionId = credentials.SubscriptionId,
            IdentityResourceId = identity.Id.ToString(),
            FederatedCredentialName = setupContext.FederatedCredentialName,
            Issuer = AzureConstants.GitHubTokenIssuer,
            Subject = setupContext.Subject,
            Audience = AzureConstants.AzureTokenExchangeAudience
        };
    }

    /// <summary>
    ///     Fails fast when required Azure credentials or GitHub repository values are missing.
    /// </summary>
    /// <param name="credentials">The connected Azure credentials used for resource preparation.</param>
    /// <param name="config">The deployment configuration used by the operation.</param>
    /// <param name="repositoryOwner">The GitHub repository owner or organization.</param>
    /// <param name="repositoryName">The GitHub repository name.</param>
    /// <param name="branchName">The repository branch used for deployment assets or workflow selection.</param>
    /// <exception cref="ArgumentException">
    ///     Azure access token is required for cloud deployment setup.
    /// </exception>
    private static void ValidateSetupInput(AzureCloudCredentialsDto credentials, DeploymentConfigDto config,
        string repositoryOwner, string repositoryName, string branchName)
    {
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

        if (string.IsNullOrWhiteSpace(config.CloudResourceGroupName))
            throw new ArgumentException("Azure resource group name is required for cloud deployment setup.",
                nameof(config));
    }

    /// <summary>
    ///     Creates the exact GitHub Actions OIDC subject for a branch ref.
    /// </summary>
    /// <param name="repositoryOwner">The GitHub repository owner or organization.</param>
    /// <param name="repositoryName">The GitHub repository name.</param>
    /// <param name="branchName">The repository branch used for deployment assets or workflow selection.</param>
    /// <returns>The git hub branch subject derived from the validated input.</returns>
    private static string CreateGitHubBranchSubject(string repositoryOwner, string repositoryName, string branchName)
    {
        return $"repo:{repositoryOwner}/{repositoryName}:ref:refs/heads/{branchName}";
    }

    /// <summary>
    ///     Creates a stable short hash used to keep generated Azure names unique and compact.
    /// </summary>
    /// <param name="value">The value to normalize, redact or validate.</param>
    /// <returns>The stable identity derived from the documented metadata or content.</returns>
    private static string CreateShortHash(string value)
    {
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)))[..10].ToLowerInvariant();
    }

    /// <summary>
    ///     Converts arbitrary repository-derived text into an Azure managed identity compatible
    ///     name.
    /// </summary>
    /// <param name="value">The value to normalize, redact or validate.</param>
    /// <returns>The normalized identity used for the documented routing or lookup.</returns>
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
