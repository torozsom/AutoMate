using Core.DTO;
using Core.Entities;
using Core.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Services.Data;
using Services.GitHub;
using Services.Templating;

namespace Services.Orchestration;

/// <summary>
///     Orchestrates cloud deployment preparation by generating IaC and workflow files and committing them to GitHub.
/// </summary>
public class CloudDeploymentOrchestrator(
    AutoMateDbContext dbContext,
    ITemplatingService templateService,
    IGitHubService gitHubService,
    ILogger<CloudDeploymentOrchestrator> logger,
    IDeploymentStatusNotifier statusNotifier)
    : ICloudDeploymentOrchestrator
{
    /// <inheritdoc />
    public async Task<Deployment> DeployCloudProjectAsync(CloudDeploymentRequestDto request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (string.IsNullOrWhiteSpace(request.RepositoryRoot))
            throw new ArgumentException("Repository root is required for cloud deployment template generation.",
                nameof(request));

        var config = request.Config;
        config.IsCloudDeployment = true;

        logger.LogInformation(
            "[CloudDeploymentOrchestrator] Starting cloud deployment preparation for project '{ProjectName}'...",
            config.ProjectName);

        var csProject = await dbContext.CsProjects.FirstOrDefaultAsync(csp => csp.Id == config.CsProjectId,
            cancellationToken);

        if (csProject == null)
        {
            logger.LogError(
                "[CloudDeploymentOrchestrator] Deployment failed: Database record for CsProject {Id} not found.",
                config.CsProjectId);
            throw new InvalidOperationException($"Project with ID {config.CsProjectId} not found in the database.");
        }

        var deployment = new Deployment
        {
            CsProjectId = csProject.Id,
            Status = DeploymentStatus.Starting
        };

        dbContext.Deployments.Add(deployment);
        await dbContext.SaveChangesAsync(cancellationToken);
        statusNotifier.NotifyStatusChanged(config.ProjectId, deployment.Status);

        try
        {
            var files = await templateService.GenerateAllTemplatesAsync(config, request.Metadata, request.CsProjectName,
                request.RepositoryRoot, cancellationToken);

            if (files.Count == 0)
                throw new InvalidOperationException("No cloud deployment templates were generated.");

            var commitSha = await gitHubService.CommitCloudDeploymentFilesAsync(request.GitHubAccessToken,
                request.RepositoryOwner, request.RepositoryName, files, request.BranchName,
                cancellationToken: cancellationToken);

            deployment.ImageTag = commitSha;
            deployment.Status = DeploymentStatus.Running;
            await dbContext.SaveChangesAsync(cancellationToken);
            statusNotifier.NotifyStatusChanged(config.ProjectId, deployment.Status);

            logger.LogInformation(
                "[CloudDeploymentOrchestrator] Cloud deployment files committed to {Owner}/{Repo}@{Branch}. Commit: {Sha}",
                request.RepositoryOwner, request.RepositoryName, request.BranchName, commitSha);

            return deployment;
        }
        catch (Exception ex)
        {
            logger.LogError(ex,
                "[CloudDeploymentOrchestrator] Cloud deployment preparation failed for project '{ProjectName}'.",
                config.ProjectName);

            deployment.Status = DeploymentStatus.Failed;
            await dbContext.SaveChangesAsync(CancellationToken.None);
            statusNotifier.NotifyStatusChanged(config.ProjectId, deployment.Status);
            throw;
        }
    }
}