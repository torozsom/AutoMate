using Application.Abstractions.Hosting;
using Application.Orchestration.Jobs;
using Domain.DTO.Azure;
using Domain.DTO.Deployment;
using Domain.DTO.Projects;

namespace Application.Orchestration.Cloud;

/// <summary>
///     Carries verified connection inputs for one cloud admission; credentials must never enter diagnostics.
/// </summary>
/// <param name="Owner">The initiating AutoMate identity.</param>
/// <param name="Config">The confirmed mutable settings, retained for compatibility with queued operations.</param>
/// <param name="RepositoryOwner">The parsed GitHub repository owner.</param>
/// <param name="RepositoryName">The parsed GitHub repository name.</param>
/// <param name="ProjectName">The existing remote project name used by templates.</param>
/// <param name="GitHubToken">The verified GitHub connection credential.</param>
/// <param name="AzureCredentials">The verified Azure connection, refreshed again by execution when required.</param>
/// <param name="IdempotencyKey">The existing page-owned SaaS admission key.</param>
public sealed record CloudDeploymentAdmissionRequest(
    Guid Owner,
    DeploymentConfigDto Config,
    string RepositoryOwner,
    string RepositoryName,
    string ProjectName,
    string GitHubToken,
    AzureCloudCredentialsDto AzureCredentials,
    string? IdempotencyKey);

/// <summary>
///     Owns profile-specific cloud request assembly and admission independently of page presentation.
/// </summary>
/// <param name="capabilities">Selects the existing SelfHosted or SaaS admission path.</param>
/// <param name="queue">Admits the existing in-memory SelfHosted job.</param>
/// <param name="runs">Authorizes and persists the existing SaaS admission.</param>
public sealed class CloudDeploymentAdmission(
    IDeploymentCapabilities capabilities,
    IDeploymentJobQueue queue,
    ICloudDeploymentRunService runs)
{
    /// <summary>
    ///     Admits a connected cloud request using unchanged metadata, defaults and credential fields.
    /// </summary>
    /// <param name="request">Verified connections and confirmed deployment settings; never log the request.</param>
    /// <param name="token">The existing admission cancellation token.</param>
    /// <returns>The SaaS receipt, or null after SelfHosted queue admission; port failures propagate unchanged.</returns>
    public async Task<CloudDeploymentReceipt?> StartAsync(CloudDeploymentAdmissionRequest request,
        CancellationToken token = default)
    {
        var metadata = new ProjectMetadataDto
        { TargetFramework = "net10.0", DotNetVersion = "10.0", IsWebProject = true };
        if (!capabilities.LocalDeploymentsEnabled)
            return await runs.StartAsync(new CloudDeploymentStart(request.Owner, request.Config.ProjectId,
                request.IdempotencyKey!, request.RepositoryOwner, request.RepositoryName, request.GitHubToken,
                request.Config, metadata, request.ProjectName, "."), token);

        await queue.EnqueueAsync(new CloudDeploymentJob(new CloudDeploymentRequestDto
        {
            RequestingUserId = request.Owner,
            Config = request.Config,
            Metadata = metadata,
            CsProjectName = request.ProjectName,
            RepositoryRoot = ".",
            GitHubAccessToken = request.GitHubToken,
            GitHubContainerRegistryToken = request.GitHubToken,
            AzureCredentials = request.AzureCredentials,
            RepositoryOwner = request.RepositoryOwner,
            RepositoryName = request.RepositoryName
        }), token);
        return null;
    }
}
