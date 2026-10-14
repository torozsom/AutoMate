using Domain.Defaults;
using Domain.DTO.Azure;
using Domain.DTO.Projects;

namespace Domain.DTO.Deployment;

/// <summary>
///     Contains the repository and project details required to generate and commit cloud
///     deployment assets.
/// </summary>
public record CloudDeploymentRequestDto
{
    /// <summary>Durable SaaS run, when launch and workflow monitoring are separated.</summary>
    public Guid? SaasRunId { get; init; }

    /// <summary>Current launch lease owner, used to reject a stale worker before committing.</summary>
    public Guid? SaasLeaseOwner { get; init; }

    /// <summary>Returns after committing rather than holding the launch worker for workflow polling.</summary>
    public bool DeferWorkflowMonitoring { get; init; }

    /// <summary>Initiating AutoMate account used to resolve current Azure monitoring authorization.</summary>
    public Guid RequestingUserId { get; init; }

    /// <summary>The deployment configuration used to render cloud deployment templates.</summary>
    public DeploymentConfigDto Config { get; init; } = new();

    /// <summary>The metadata extracted from the selected C# project.</summary>
    public ProjectMetadataDto Metadata { get; init; } = new();

    /// <summary>The selected C# project name without the .csproj extension.</summary>
    public string CsProjectName { get; init; } = string.Empty;

    /// <summary>The repository root path used as the relative-path anchor while rendering templates.</summary>
    public string RepositoryRoot { get; init; } = string.Empty;

    /// <summary>
    ///     Repository-write access token used for launch preparation; never include it in
    ///     diagnostics.
    /// </summary>
    public string GitHubAccessToken { get; init; } = string.Empty;

    /// <summary>Azure credentials for configuring the GitHub Actions OIDC trust.</summary>
    public AzureCloudCredentialsDto AzureCredentials { get; init; } = new();

    /// <summary>
    ///     GHCR credential supplied to supported legacy workflows; never include it in diagnostics.
    /// </summary>
    public string GitHubContainerRegistryToken { get; init; } = string.Empty;

    /// <summary>The GitHub repository owner or organization name.</summary>
    public string RepositoryOwner { get; init; } = string.Empty;

    /// <summary>The GitHub repository name.</summary>
    public string RepositoryName { get; init; } = string.Empty;

    /// <summary>The branch where AutoMate should commit generated cloud deployment files.</summary>
    public string BranchName { get; init; } = DeploymentDefaults.CloudDeploymentBranchName;

    /// <summary>The generated workflow file name to dispatch and poll after committing templates.</summary>
    public string WorkflowFileName { get; init; } = DeploymentDefaults.CloudWorkflowFileName;
}
