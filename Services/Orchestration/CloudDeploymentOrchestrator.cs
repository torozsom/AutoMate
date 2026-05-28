using Core.DTO;
using Core.Entities;
using Core.Enums;
using Microsoft.EntityFrameworkCore;
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
public class CloudDeploymentOrchestrator(
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
        ApplyCloudDefaults(config);

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
        await StreamBuildLogAsync(config.ProjectId,
            $"Starting cloud deployment preparation for {request.RepositoryOwner}/{request.RepositoryName}@{request.BranchName}.");

        try
        {
            var oidcSetup = await azureDeploymentOrchestrator.EnsureFederatedIdentityAsync(request.AzureCredentials,
                config, request.RepositoryOwner, request.RepositoryName, request.BranchName, cancellationToken);
            await StreamBuildLogAsync(config.ProjectId, "Azure OIDC trust configured for GitHub Actions.");

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
            await StreamBuildLogAsync(config.ProjectId, "GitHub Actions repository secrets upserted.");

            var files = await templateService.GenerateAllTemplatesAsync(config, request.Metadata, request.CsProjectName,
                request.RepositoryRoot, cancellationToken);

            if (files.Count == 0)
                throw new InvalidOperationException("No cloud deployment templates were generated.");

            await StreamBuildLogAsync(config.ProjectId,
                $"Generated {files.Count} cloud deployment file(s): {string.Join(", ", files.Select(f => f.Path))}.");

            var commitSha = await gitHubService.CommitCloudDeploymentFilesAsync(request.GitHubAccessToken,
                request.RepositoryOwner, request.RepositoryName, files, request.BranchName,
                cancellationToken: cancellationToken);
            await StreamBuildLogAsync(config.ProjectId,
                $"Committed cloud deployment files to {request.RepositoryOwner}/{request.RepositoryName}@{request.BranchName}. Commit: {commitSha}");

            await StreamBuildLogAsync(config.ProjectId,
                "GitHub Actions workflow will start from the deployment branch push trigger.");

            deployment.ImageTag = commitSha;
            deployment.Status = DeploymentStatus.Running;
            await dbContext.SaveChangesAsync(cancellationToken);
            statusNotifier.NotifyStatusChanged(config.ProjectId, deployment.Status);

            var workflowRun = await PollWorkflowRunAsync(request, commitSha, cancellationToken);
            if (workflowRun != null)
            {
                deployment.CloudGitHubActionRunId = workflowRun.Id;
                await dbContext.SaveChangesAsync(cancellationToken);
            }

            if (workflowRun is { Status: "completed" } &&
                !string.Equals(workflowRun.Conclusion, "success", StringComparison.OrdinalIgnoreCase))
            {
                deployment.Status = DeploymentStatus.Failed;
                await dbContext.SaveChangesAsync(cancellationToken);
                statusNotifier.NotifyStatusChanged(config.ProjectId, deployment.Status);
                await StreamBuildLogAsync(config.ProjectId,
                    $"GitHub Actions workflow failed. Details: {workflowRun.HtmlUrl}");
                await StreamWorkflowLogsAsync(request, workflowRun.Id, config.ProjectId, cancellationToken);
            }
            else if (workflowRun is { Status: "completed" } &&
                     string.Equals(workflowRun.Conclusion, "success", StringComparison.OrdinalIgnoreCase))
            {
                await StreamBuildLogAsync(config.ProjectId,
                    $"GitHub Actions workflow completed successfully. Details: {workflowRun.HtmlUrl}");
                await StreamWorkflowLogsAsync(request, workflowRun.Id, config.ProjectId, cancellationToken);
                azureContainerAppRuntimeStreamer.StartStreaming(request.AzureCredentials, config);
            }
            else
            {
                await StreamBuildLogAsync(config.ProjectId,
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

    private async Task<GitHubWorkflowRunDto?> PollWorkflowRunAsync(CloudDeploymentRequestDto request, string commitSha,
        CancellationToken cancellationToken)
    {
        const int maxAttempts = 60;
        GitHubWorkflowRunDto? latestRun = null;
        string? lastStatusMessage = null;

        for (var attempt = 0; attempt < maxAttempts; attempt++)
        {
            var run = await gitHubService.GetLatestWorkflowRunAsync(request.GitHubAccessToken, request.RepositoryOwner,
                request.RepositoryName, request.WorkflowFileName, request.BranchName, commitSha, cancellationToken);

            if (run == null)
            {
                await Task.Delay(TimeSpan.FromSeconds(10), cancellationToken);
                continue;
            }

            latestRun = run;

            logger.LogInformation(
                "[CloudDeploymentOrchestrator] GitHub workflow run {RunId} for {Owner}/{Repo}@{Branch}: {Status}/{Conclusion}. {Url}",
                run.Id, request.RepositoryOwner, request.RepositoryName, request.BranchName, run.Status,
                run.Conclusion ?? "pending", run.HtmlUrl);

            var statusMessage = $"{run.Status}/{run.Conclusion ?? "pending"}";
            if (!string.Equals(statusMessage, lastStatusMessage, StringComparison.OrdinalIgnoreCase))
            {
                await StreamBuildLogAsync(request.Config.ProjectId,
                    $"GitHub Actions run {run.Id}: {statusMessage}. {run.HtmlUrl}");
                lastStatusMessage = statusMessage;
            }

            if (string.Equals(run.Status, "completed", StringComparison.OrdinalIgnoreCase))
                return run;

            await Task.Delay(TimeSpan.FromSeconds(10), cancellationToken);
        }

        return latestRun;
    }


    private async Task StreamWorkflowLogsAsync(CloudDeploymentRequestDto request, long runId, Guid projectId,
        CancellationToken cancellationToken)
    {
        try
        {
            var logs = await gitHubService.DownloadWorkflowRunLogsAsync(request.GitHubAccessToken,
                request.RepositoryOwner, request.RepositoryName, runId, cancellationToken);

            if (!string.IsNullOrWhiteSpace(logs))
                await logStreamer.StreamBuildLogsAsync(projectId, logs);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex,
                "[CloudDeploymentOrchestrator] Failed to download GitHub Actions logs for run {RunId}.", runId);
        }
    }


    private async Task StreamBuildLogAsync(Guid projectId, string message)
    {
        await logStreamer.StreamBuildLogsAsync(projectId, $"[cloud] {message}\r\n");
    }


    private static void ApplyCloudDefaults(DeploymentConfigDto config)
    {
        var resourceName = NormalizeResourceName(config.ProjectName);
        var environmentSuffix = GetEnvironmentSuffix(config.EnvironmentName);
        var baseName = $"{resourceName}-{environmentSuffix}";

        if (string.IsNullOrWhiteSpace(config.CloudAzureRegion))
            config.CloudAzureRegion = "eastus";

        if (string.IsNullOrWhiteSpace(config.CloudResourceGroupName))
            config.CloudResourceGroupName = $"{baseName}-rg";

        if (string.IsNullOrWhiteSpace(config.CloudContainerAppName))
            config.CloudContainerAppName = $"{baseName}-app";

        if (string.IsNullOrWhiteSpace(config.CloudRegistryName))
            config.CloudRegistryName = "ghcr.io";
    }


    private static string GetEnvironmentSuffix(string environmentName)
    {
        var normalized = environmentName.Trim().ToLowerInvariant();

        return normalized switch
        {
            "production" => "prod",
            "staging" => "stg",
            "development" => "dev",
            _ when normalized.Length > 0 => NormalizeResourceName(normalized),
            _ => "dev"
        };
    }


    private static string NormalizeResourceName(string value)
    {
        var normalized = new string(value
            .Trim()
            .ToLowerInvariant()
            .Select(c => char.IsLetterOrDigit(c) ? c : '-')
            .ToArray());

        normalized = string.Join('-', normalized
            .Split('-', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

        if (string.IsNullOrWhiteSpace(normalized))
            normalized = "automate-app";

        return normalized.Length <= 23 ? normalized : normalized[..23].TrimEnd('-');
    }
}
