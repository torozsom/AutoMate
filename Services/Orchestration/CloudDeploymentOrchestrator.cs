using Core.DTO;
using Core.Entities;
using Core.Enums;
using System.Text;
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

        if (string.IsNullOrWhiteSpace(request.RepositoryOwner))
            throw new ArgumentException("Repository owner is required for cloud deployment.", nameof(request));

        if (string.IsNullOrWhiteSpace(request.RepositoryName))
            throw new ArgumentException("Repository name is required for cloud deployment.", nameof(request));

        if (string.IsNullOrWhiteSpace(request.GitHubAccessToken))
            throw new ArgumentException("GitHub access token is required for cloud deployment.", nameof(request));

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

            await StreamBuildLogAsync(config.ProjectId,
                $"Azure OIDC trust configured for GitHub Actions. Identity: {oidcSetup.IdentityResourceId}. Federated credential: {oidcSetup.FederatedCredentialName}. Subject: {oidcSetup.Subject}. Audience: {oidcSetup.Audience}.");

            if (string.IsNullOrWhiteSpace(oidcSetup.ClientId) ||
                string.IsNullOrWhiteSpace(oidcSetup.TenantId) ||
                string.IsNullOrWhiteSpace(oidcSetup.SubscriptionId))
                throw new InvalidOperationException("Azure OIDC setup did not return complete credentials.");

            var repositorySecrets = BuildRepositorySecrets(request, oidcSetup);

            await gitHubService.UpsertRepositorySecretsAsync(request.GitHubAccessToken, request.RepositoryOwner,
                request.RepositoryName, repositorySecrets, cancellationToken);

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


    /// <summary>
    ///     Retrieves an existing CsProject for the cloud deployment or creates a new one if it doesn't exist.
    /// </summary>
    /// <param name="request">The cloud deployment request.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The CsProject instance.</returns>
    /// <exception cref="InvalidOperationException">Thrown if the project ID is invalid or project not found.</exception>
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


    /// <summary>
    ///     Polls GitHub for the latest workflow run associated with the given
    ///     commit SHA until it completes or a timeout is reached.
    /// </summary>
    /// <param name="request">The cloud deployment request.</param>
    /// <param name="commitSha">The commit SHA.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The latest workflow run, or null if not found.</returns>
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


    /// <summary>
    ///     Downloads the logs for the specified GitHub Actions workflow run and streams them to the client.
    /// </summary>
    /// <param name="request">The cloud deployment request.</param>
    /// <param name="runId">The workflow run ID.</param>
    /// <param name="projectId">The project ID.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
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


    /// <summary>
    ///     Streams a log message to the client with a consistent prefix for cloud deployment logs.
    /// </summary>
    /// <param name="projectId">The project ID.</param>
    /// <param name="message">The log message.</param>
    private async Task StreamBuildLogAsync(Guid projectId, string message)
    {
        await logStreamer.StreamBuildLogsAsync(projectId, $"[cloud] {message}\r\n");
    }


    /// <summary>
    ///     Builds the repository secrets consumed by the generated GitHub Actions workflow.
    /// </summary>
    /// <param name="request">The cloud deployment request.</param>
    /// <param name="oidcSetup">The Azure OIDC setup result.</param>
    /// <returns>The GitHub Actions repository secrets to create or update.</returns>
    private static Dictionary<string, string> BuildRepositorySecrets(CloudDeploymentRequestDto request,
        AzureOidcSetupResultDto oidcSetup)
    {
        var secrets = new Dictionary<string, string>
        {
            ["AZURE_CLIENT_ID"] = oidcSetup.ClientId,
            ["AZURE_TENANT_ID"] = oidcSetup.TenantId,
            ["AZURE_SUBSCRIPTION_ID"] = oidcSetup.SubscriptionId,
            ["GHCR_PAT"] = string.IsNullOrWhiteSpace(request.GitHubContainerRegistryToken)
                ? request.GitHubAccessToken
                : request.GitHubContainerRegistryToken
        };

        foreach (var (database, index) in request.Config.Databases.Select((database, index) => (database, index)))
        {
            if (!RequiresDatabaseLogin(database.DbType))
                continue;

            secrets[CloudDeploymentSecretNames.GetDatabaseUsernameSecretName(index)] =
                Base64Encode(string.IsNullOrWhiteSpace(database.DbUser) ? "automateadmin" : database.DbUser.Trim());
            secrets[CloudDeploymentSecretNames.GetDatabasePasswordSecretName(index)] =
                Base64Encode(database.DbPassword ?? string.Empty);
        }

        foreach (var (envVar, index) in request.Config.CustomEnvVars
                     .Where(kvp => !string.IsNullOrWhiteSpace(kvp.Key))
                     .OrderBy(kvp => kvp.Key, StringComparer.OrdinalIgnoreCase)
                     .Select((envVar, index) => (envVar, index)))
            secrets[CloudDeploymentSecretNames.GetCustomEnvironmentSecretName(index, envVar.Key.Trim())] =
                Base64Encode(envVar.Value ?? string.Empty);

        return secrets;
    }


    private static string Base64Encode(string value)
    {
        return Convert.ToBase64String(Encoding.UTF8.GetBytes(value));
    }


    private static bool RequiresDatabaseLogin(string databaseType)
    {
        return databaseType.Trim().ToLowerInvariant() is "postgresql" or "postgres" or "mysql" or "sqlserver"
            or "sql-server" or "mssql" or "microsoft sql server";
    }


    /// <summary>
    ///     Applies default values to the deployment configuration for any missing cloud-related settings.
    /// </summary>
    /// <param name="config">The deployment configuration.</param>
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


    /// <summary>
    ///     Generates a short suffix for resource names based on the environment name, using common
    ///     abbreviations for known environments and normalized values for custom ones.
    /// </summary>
    /// <param name="environmentName">The environment name.</param>
    /// <returns>The environment suffix.</returns>
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


    /// <summary>
    ///     Normalizes a string to be used in resource names by converting to lowercase, replacing non-alphanumeric
    ///     characters with dashes, collapsing multiple dashes, and trimming to a maximum length of 23 characters.
    /// </summary>
    /// <param name="value">The string to normalize.</param>
    /// <returns>The normalized resource name.</returns>
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
