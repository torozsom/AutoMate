using Core.DTO;
using Core.Entities;
using Core.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Services.Azure;
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
    IAzureDeploymentOrchestrator azureDeploymentOrchestrator,
    IAzureContainerAppRuntimeStreamer azureContainerAppRuntimeStreamer,
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

        var csProject = await GetOrCreateCloudCsProjectAsync(request, cancellationToken);
        config.CsProjectId = csProject.Id;

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
            var oidcSetup = await azureDeploymentOrchestrator.EnsureFederatedIdentityAsync(request.AzureCredentials,
                config, request.RepositoryOwner, request.RepositoryName, request.BranchName, cancellationToken);

            await gitHubService.UpsertRepositorySecretsAsync(request.GitHubAccessToken, request.RepositoryOwner,
                request.RepositoryName, new Dictionary<string, string>
                {
                    ["AZURE_CLIENT_ID"] = oidcSetup.ClientId,
                    ["AZURE_TENANT_ID"] = oidcSetup.TenantId,
                    ["AZURE_SUBSCRIPTION_ID"] = oidcSetup.SubscriptionId,
                    ["GHCR_PAT"] = string.IsNullOrWhiteSpace(request.GitHubContainerRegistryToken)
                        ? request.GitHubAccessToken
                        : request.GitHubContainerRegistryToken
                }, cancellationToken);

            var files = await templateService.GenerateAllTemplatesAsync(config, request.Metadata, request.CsProjectName,
                request.RepositoryRoot, cancellationToken);

            if (files.Count == 0)
                throw new InvalidOperationException("No cloud deployment templates were generated.");

            var commitSha = await gitHubService.CommitCloudDeploymentFilesAsync(request.GitHubAccessToken,
                request.RepositoryOwner, request.RepositoryName, files, request.BranchName,
                cancellationToken: cancellationToken);

            await DispatchWorkflowWithRetryAsync(request, cancellationToken);

            deployment.ImageTag = commitSha;
            deployment.Status = DeploymentStatus.Running;
            await dbContext.SaveChangesAsync(cancellationToken);
            statusNotifier.NotifyStatusChanged(config.ProjectId, deployment.Status);

            var workflowRun = await PollWorkflowRunAsync(request, commitSha, cancellationToken);
            if (workflowRun is { Status: "completed" } &&
                !string.Equals(workflowRun.Conclusion, "success", StringComparison.OrdinalIgnoreCase))
            {
                deployment.Status = DeploymentStatus.Failed;
                await dbContext.SaveChangesAsync(cancellationToken);
                statusNotifier.NotifyStatusChanged(config.ProjectId, deployment.Status);
            }
            else
            {
                azureContainerAppRuntimeStreamer.StartStreaming(request.AzureCredentials, config);
            }

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

    private async Task<CsProject> GetOrCreateCloudCsProjectAsync(CloudDeploymentRequestDto request,
        CancellationToken cancellationToken)
    {
        if (request.Config.CsProjectId != Guid.Empty)
        {
            var existingCsProject = await dbContext.CsProjects.FirstOrDefaultAsync(
                csp => csp.Id == request.Config.CsProjectId, cancellationToken);

            if (existingCsProject == null)
                throw new InvalidOperationException(
                    $"Project with ID {request.Config.CsProjectId} not found in the database.");

            return existingCsProject;
        }

        var app = await dbContext.Applications
            .Include(a => a.CsProjects)
            .FirstOrDefaultAsync(a => a.Id == request.Config.ProjectId, cancellationToken);

        if (app == null)
            throw new InvalidOperationException($"Application with ID {request.Config.ProjectId} not found.");

        var csProject = app.CsProjects.FirstOrDefault(csp => csp.IsWebProject);
        if (csProject != null)
            return csProject;

        csProject = new CsProject
        {
            AppId = app.Id,
            Name = string.IsNullOrWhiteSpace(request.CsProjectName) ? app.Name : request.CsProjectName,
            Path = request.RepositoryRoot,
            IsWebProject = true
        };

        dbContext.CsProjects.Add(csProject);
        await dbContext.SaveChangesAsync(cancellationToken);
        return csProject;
    }

    private async Task DispatchWorkflowWithRetryAsync(CloudDeploymentRequestDto request,
        CancellationToken cancellationToken)
    {
        const int maxAttempts = 3;

        for (var attempt = 1; attempt <= maxAttempts; attempt++)
            try
            {
                await gitHubService.DispatchWorkflowAsync(request.GitHubAccessToken, request.RepositoryOwner,
                    request.RepositoryName, request.WorkflowFileName, request.BranchName, cancellationToken);
                return;
            }
            catch (HttpRequestException) when (attempt < maxAttempts)
            {
                await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken);
            }
    }

    private async Task<GitHubWorkflowRunDto?> PollWorkflowRunAsync(CloudDeploymentRequestDto request, string commitSha,
        CancellationToken cancellationToken)
    {
        const int maxAttempts = 12;

        for (var attempt = 0; attempt < maxAttempts; attempt++)
        {
            var run = await gitHubService.GetLatestWorkflowRunAsync(request.GitHubAccessToken, request.RepositoryOwner,
                request.RepositoryName, request.WorkflowFileName, request.BranchName, commitSha, cancellationToken);

            if (run == null)
            {
                await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken);
                continue;
            }

            logger.LogInformation(
                "[CloudDeploymentOrchestrator] GitHub workflow run {RunId} for {Owner}/{Repo}@{Branch}: {Status}/{Conclusion}. {Url}",
                run.Id, request.RepositoryOwner, request.RepositoryName, request.BranchName, run.Status,
                run.Conclusion ?? "pending", run.HtmlUrl);

            if (string.Equals(run.Status, "completed", StringComparison.OrdinalIgnoreCase))
                return run;

            await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken);
        }

        return null;
    }
}