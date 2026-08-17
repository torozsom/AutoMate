using Core.DTO;
using Core.Entities;
using Core.Enums;
using Microsoft.Extensions.Logging;
using Services.Azure;
using Services.Data;
using Services.GitHub;
using Services.LogStreaming;
using Services.Templating;

namespace Services.Orchestration;

/// <summary>
///     Orchestrates cloud deployment preparation by generating IaC and workflow files and committing them to GitHub.
/// </summary>
public sealed class CloudDeploymentOrchestrator(
    AutoMateDbContext dbContext,
    ITemplatingService templateService,
    IGitHubService gitHubService,
    IAzureDeploymentOrchestrator azureDeploymentOrchestrator,
    IAzureContainerAppRuntimeStreamer azureContainerAppRuntimeStreamer,
    ILogStreamer logStreamer,
    ILogger<CloudDeploymentOrchestrator> logger,
    IDeploymentStatusNotifier statusNotifier)
    : ICloudDeploymentOrchestrator
{
    /// <summary>
    ///     Resolves or creates the C# project associated with cloud deployments.
    /// </summary>
    private readonly CloudCsProjectResolver _csProjectResolver = new(dbContext);

    /// <summary>
    ///     Persists cloud deployment status transitions and notifies UI subscribers.
    /// </summary>
    private readonly DeploymentStatusUpdater _statusUpdater =
        new(dbContext, statusNotifier, logger, nameof(CloudDeploymentOrchestrator));

    /// <summary>
    ///     Polls GitHub Actions and streams cloud deployment logs.
    /// </summary>
    private readonly GitHubWorkflowMonitor _workflowMonitor = new(gitHubService, logStreamer, logger);

    /// <inheritdoc />
    public async Task<Deployment> DeployCloudProjectAsync(CloudDeploymentRequestDto request,
        CancellationToken cancellationToken = default)
    {
        CloudDeploymentRequestValidator.Validate(request);

        var config = request.Config;
        config.IsCloudDeployment = true;
        CloudDeploymentDefaults.Apply(config);

        logger.LogInformation(
            "[CloudDeploymentOrchestrator] Starting cloud deployment preparation for project '{ProjectName}'...",
            config.ProjectName);

        var csProject = await _csProjectResolver.GetOrCreateAsync(request, cancellationToken);
        config.CsProjectId = csProject.Id;

        var deployment = new Deployment
        {
            CsProjectId = csProject.Id,
            Status = DeploymentStatus.Starting
        };

        dbContext.Deployments.Add(deployment);
        await dbContext.SaveChangesAsync(cancellationToken);
        statusNotifier.NotifyStatusChanged(config.ProjectId, deployment.Status);
        await _workflowMonitor.StreamBuildLogAsync(config.ProjectId,
            $"Starting cloud deployment preparation for {request.RepositoryOwner}/{request.RepositoryName}@{request.BranchName}.");

        try
        {
            var oidcSetup = await azureDeploymentOrchestrator.EnsureFederatedIdentityAsync(request.AzureCredentials,
                config, request.RepositoryOwner, request.RepositoryName, request.BranchName, cancellationToken);

            await _workflowMonitor.StreamBuildLogAsync(config.ProjectId,
                $"Azure OIDC trust configured for GitHub Actions. Identity: {oidcSetup.IdentityResourceId}. Federated credential: {oidcSetup.FederatedCredentialName}. Subject: {oidcSetup.Subject}. Audience: {oidcSetup.Audience}.");

            if (string.IsNullOrWhiteSpace(oidcSetup.ClientId) ||
                string.IsNullOrWhiteSpace(oidcSetup.TenantId) ||
                string.IsNullOrWhiteSpace(oidcSetup.SubscriptionId))
                throw new InvalidOperationException("Azure OIDC setup did not return complete credentials.");

            var repositorySecrets = CloudRepositorySecretBuilder.Build(request, oidcSetup);

            await gitHubService.UpsertRepositorySecretsAsync(request.GitHubAccessToken, request.RepositoryOwner,
                request.RepositoryName, repositorySecrets, cancellationToken);

            await _workflowMonitor.StreamBuildLogAsync(config.ProjectId, "GitHub Actions repository secrets upserted.");

            var files = await templateService.GenerateAllTemplatesAsync(config, request.Metadata, request.CsProjectName,
                request.RepositoryRoot, cancellationToken);

            if (files.Count == 0)
                throw new InvalidOperationException("No cloud deployment templates were generated.");

            await _workflowMonitor.StreamBuildLogAsync(config.ProjectId,
                $"Generated {files.Count} cloud deployment file(s): {string.Join(", ", files.Select(f => f.Path))}.");

            var commitSha = await gitHubService.CommitCloudDeploymentFilesAsync(request.GitHubAccessToken,
                request.RepositoryOwner, request.RepositoryName, files, request.BranchName,
                cancellationToken: cancellationToken);
            await _workflowMonitor.StreamBuildLogAsync(config.ProjectId,
                $"Committed cloud deployment files to {request.RepositoryOwner}/{request.RepositoryName}@{request.BranchName}. Commit: {commitSha}");

            await _workflowMonitor.StreamBuildLogAsync(config.ProjectId,
                "GitHub Actions workflow will start from the deployment branch push trigger.");

            deployment.ImageTag = commitSha;
            await _statusUpdater.UpdateAsync(config.ProjectId, deployment, DeploymentStatus.Running,
                cancellationToken);

            var workflowRun = await _workflowMonitor.PollWorkflowRunAsync(request, commitSha, cancellationToken);
            if (workflowRun != null)
            {
                deployment.CloudGitHubActionRunId = workflowRun.Id;
                await dbContext.SaveChangesAsync(cancellationToken);
            }

            if (workflowRun is { Status: "completed" } &&
                !string.Equals(workflowRun.Conclusion, "success", StringComparison.OrdinalIgnoreCase))
            {
                await _statusUpdater.UpdateAsync(config.ProjectId, deployment, DeploymentStatus.Failed,
                    cancellationToken);
                await _workflowMonitor.StreamBuildLogAsync(config.ProjectId,
                    $"GitHub Actions workflow failed. Details: {workflowRun.HtmlUrl}");
                await _workflowMonitor.StreamWorkflowLogsAsync(request, workflowRun.Id, config.ProjectId,
                    cancellationToken);
            }
            else if (workflowRun is { Status: "completed" } &&
                     string.Equals(workflowRun.Conclusion, "success", StringComparison.OrdinalIgnoreCase))
            {
                await _workflowMonitor.StreamBuildLogAsync(config.ProjectId,
                    $"GitHub Actions workflow completed successfully. Details: {workflowRun.HtmlUrl}");
                await _workflowMonitor.StreamWorkflowLogsAsync(request, workflowRun.Id, config.ProjectId,
                    cancellationToken);
                azureContainerAppRuntimeStreamer.StartStreaming(request.AzureCredentials, config);
            }
            else
            {
                await _workflowMonitor.StreamBuildLogAsync(config.ProjectId,
                    "GitHub Actions workflow is still queued or running. Refresh the project details page for the latest persisted status.");
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
}