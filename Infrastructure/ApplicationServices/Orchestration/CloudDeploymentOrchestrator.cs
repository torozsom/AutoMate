using Application.Abstractions.Azure;
using Application.Abstractions.Diagnostics;
using Application.Abstractions.GitHub;
using Application.Abstractions.Hosting;
using Application.Abstractions.Templating;
using Application.Diagnostics;
using Domain.DTO;
using Domain.Entities;
using Domain.Enums;
using Infrastructure.Azure;
using Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Application.Orchestration;

/// <summary>
///     Orchestrates cloud deployment preparation by generating IaC and workflow files and committing them to GitHub.
/// </summary>
public sealed class CloudDeploymentOrchestrator(
    AutoMateDbContext dbContext,
    ITemplatingService templateService,
    IGitHubService gitHubService,
    IGitHubAppCredentials githubApp,
    IAzureDeploymentOrchestrator azureDeploymentOrchestrator,
    IAzureContainerAppRuntimeStreamer azureContainerAppRuntimeStreamer,
    IDeploymentCapabilities capabilities,
    IDeploymentDiagnosticPublisher diagnostics,
    IDiagnosticRedactor redactor,
    IOptions<GitHubWorkflowMonitoringOptions> workflowMonitoringOptions,
    ILogger<CloudDeploymentOrchestrator> logger,
    ILoggerFactory loggerFactory,
    IDeploymentStatusNotifier statusNotifier,
    AzureArmCredentialsProvider azureCredentials)
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
    private readonly GitHubWorkflowMonitor _workflowMonitor = new(dbContext, gitHubService, diagnostics, redactor,
        workflowMonitoringOptions.Value, loggerFactory.CreateLogger<GitHubWorkflowMonitor>());

    /// <inheritdoc />
    public async Task<Deployment> DeployCloudProjectAsync(CloudDeploymentRequestDto request,
        CancellationToken cancellationToken = default)
    {
        if (!capabilities.CloudDeploymentsEnabled)
            throw new InvalidOperationException("Cloud deployments are disabled for this AutoMate instance.");

        CloudDeploymentRequestValidator.Validate(request);

        var config = request.Config;
        config.IsCloudDeployment = true;
        CloudDeploymentDefaults.Apply(config);

        using var projectScope = OperationalLog.BeginCorrelation(logger, projectId: config.ProjectId);
        logger.LogInformation("Starting cloud deployment preparation.");

        var csProject = await _csProjectResolver.GetOrCreateAsync(request, cancellationToken);
        config.CsProjectId = csProject.Id;

        CloudDeploymentRun? saasRun = null;
        if (request.SaasRunId is { } runId)
            saasRun = await dbContext.CloudDeploymentRuns.SingleAsync(item => item.Id == runId, cancellationToken);
        var deployment = saasRun?.DeploymentId is { } existingDeploymentId
            ? await dbContext.Deployments.SingleAsync(item => item.Id == existingDeploymentId, cancellationToken)
            : new Deployment { CsProjectId = csProject.Id, Status = DeploymentStatus.Starting };
        if (saasRun?.DeploymentId is null)
        {
            dbContext.Deployments.Add(deployment);
            if (saasRun is not null) saasRun.DeploymentId = deployment.Id;
            await dbContext.SaveChangesAsync(cancellationToken);
        }

        using var deploymentScope = OperationalLog.BeginCorrelation(logger, deployment.Id);
        statusNotifier.NotifyStatusChanged(config.ProjectId, deployment.Status);
        try
        {
            await _workflowMonitor.StreamBuildLogAsync(deployment.Id, config.ProjectId,
                $"Starting cloud deployment preparation for {request.RepositoryOwner}/{request.RepositoryName}@{request.BranchName}.");
            // Self-hosted queued requests can contain an expired login token by the time a launch starts.
            if (saasRun is null)
                request = request with
                {
                    AzureCredentials = await azureCredentials.GetAsync(request.RequestingUserId, cancellationToken)
                };
            var oidcSetup = await azureDeploymentOrchestrator.EnsureFederatedIdentityAsync(request.AzureCredentials,
                config, request.RepositoryOwner, request.RepositoryName, request.BranchName, cancellationToken);

            await _workflowMonitor.StreamBuildLogAsync(deployment.Id, config.ProjectId,
                $"Azure OIDC trust configured for GitHub Actions. Identity: {oidcSetup.IdentityResourceId}. Federated credential: {oidcSetup.FederatedCredentialName}. Subject: {oidcSetup.Subject}. Audience: {oidcSetup.Audience}.");

            if (string.IsNullOrWhiteSpace(oidcSetup.ClientId) ||
                string.IsNullOrWhiteSpace(oidcSetup.TenantId) ||
                string.IsNullOrWhiteSpace(oidcSetup.SubscriptionId))
                throw new InvalidOperationException("Azure OIDC setup did not return complete credentials.");

            // Azure provisioning can outlast an installation token; mint one immediately before GitHub calls.
            if (saasRun is not null)
                request = request with
                {
                    GitHubAccessToken = await githubApp.CreateInstallationTokenAsync(saasRun.InstallationId,
                        cancellationToken)
                };

            var repositorySecrets = CloudRepositorySecretBuilder.Build(request, oidcSetup);

            await gitHubService.UpsertRepositorySecretsAsync(request.GitHubAccessToken, request.RepositoryOwner,
                request.RepositoryName, repositorySecrets, cancellationToken);

            await _workflowMonitor.StreamBuildLogAsync(deployment.Id, config.ProjectId,
                "GitHub Actions repository secrets upserted.");

            var files = await templateService.GenerateAllTemplatesAsync(config, request.Metadata, request.CsProjectName,
                request.RepositoryRoot, cancellationToken);

            if (files.Count == 0)
                throw new InvalidOperationException("No cloud deployment templates were generated.");

            await _workflowMonitor.StreamBuildLogAsync(deployment.Id, config.ProjectId,
                $"Generated {files.Count} cloud deployment file(s): {string.Join(", ", files.Select(f => f.Path))}.");

            if (saasRun is not null)
                request = request with
                {
                    GitHubAccessToken = await githubApp.CreateInstallationTokenAsync(saasRun.InstallationId,
                        cancellationToken)
                };

            string commitSha;
            if (saasRun is not null)
            {
                // A recovered worker must finish checking the marker before another worker can commit
                // this run. The transaction-scoped advisory lock also protects the uncertain-commit case.
                await using var commitTransaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);
                var leaseBytes = saasRun.Id.ToByteArray();
                var lockKeyA = BitConverter.ToInt32(leaseBytes, 0);
                var lockKeyB = BitConverter.ToInt32(leaseBytes, 4);
                await dbContext.Database.SqlQuery<int>($"""
                                                        SELECT 1 AS "Value" FROM pg_advisory_xact_lock({lockKeyA}, {lockKeyB})
                                                        """).SingleAsync(cancellationToken);
                await dbContext.Entry(saasRun).ReloadAsync(cancellationToken);
                if (saasRun.LeaseOwner != request.SaasLeaseOwner ||
                    saasRun.Phase != CloudRunPhase.Preparing)
                    throw new InvalidOperationException("The launch lease was lost before the GitHub commit.");
                var marker = $"AutoMate-Run: {saasRun.Id:N}";
                commitSha = saasRun.Attempt > 1
                    ? await gitHubService.FindDeploymentCommitAsync(
                        request.GitHubAccessToken, request.RepositoryOwner, request.RepositoryName,
                        request.BranchName, marker, cancellationToken) ?? string.Empty
                    : string.Empty;
                if (string.IsNullOrEmpty(commitSha))
                    commitSha =
                        await gitHubService.CommitCloudDeploymentFilesAsync(request.GitHubAccessToken,
                            request.RepositoryOwner, request.RepositoryName, files, request.BranchName,
                            $"Add AutoMate Azure deployment workflow\n\n{marker}",
                            cancellationToken);
                deployment.ImageTag = commitSha;
                saasRun.CommitSha = commitSha;
                saasRun.CommittedAt = DateTimeOffset.UtcNow;
                saasRun.Phase = CloudRunPhase.AwaitingWorkflow;
                saasRun.LeaseOwner = null;
                saasRun.LeaseUntil = null;
                await dbContext.SaveChangesAsync(cancellationToken);
                await commitTransaction.CommitAsync(cancellationToken);
            }
            else
            {
                commitSha = await gitHubService.CommitCloudDeploymentFilesAsync(request.GitHubAccessToken,
                    request.RepositoryOwner, request.RepositoryName, files, request.BranchName,
                    cancellationToken: cancellationToken);
                deployment.ImageTag = commitSha;
            }

            await _workflowMonitor.StreamBuildLogAsync(deployment.Id, config.ProjectId,
                $"Committed cloud deployment files to {request.RepositoryOwner}/{request.RepositoryName}@{request.BranchName}. Commit: {commitSha}");

            await _workflowMonitor.StreamBuildLogAsync(deployment.Id, config.ProjectId,
                "GitHub Actions workflow will start from the deployment branch push trigger.");

            if (request.DeferWorkflowMonitoring)
                return deployment;
            await dbContext.SaveChangesAsync(cancellationToken);

            var workflowRun = await _workflowMonitor.PollWorkflowRunAsync(request, deployment, commitSha,
                cancellationToken);
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
                await _workflowMonitor.StreamBuildLogAsync(deployment.Id, config.ProjectId,
                    $"GitHub Actions workflow failed. Details: {workflowRun.HtmlUrl}");
            }
            else if (workflowRun is { Status: "completed" } &&
                     string.Equals(workflowRun.Conclusion, "success", StringComparison.OrdinalIgnoreCase))
            {
                await _statusUpdater.UpdateAsync(config.ProjectId, deployment, DeploymentStatus.Running,
                    cancellationToken);
                await _workflowMonitor.StreamBuildLogAsync(deployment.Id, config.ProjectId,
                    $"GitHub Actions workflow completed successfully. Details: {workflowRun.HtmlUrl}");
                deployment.CloudContainerAppName = config.CloudContainerAppName;
                deployment.CloudContainerRevision = $"{config.CloudContainerAppName}--am-{workflowRun.Id}";
                deployment.CloudResourceId =
                    $"/subscriptions/{Uri.EscapeDataString(request.AzureCredentials.SubscriptionId)}/resourceGroups/{Uri.EscapeDataString(config.CloudResourceGroupName)}/providers/Microsoft.App/containerApps/{Uri.EscapeDataString(config.CloudContainerAppName)}";
                await dbContext.SaveChangesAsync(cancellationToken);
                azureContainerAppRuntimeStreamer.StartStreaming(new AzureContainerAppRuntimeStreamRequest
                {
                    ProjectId = config.ProjectId,
                    DeploymentId = deployment.Id,
                    UserId = request.RequestingUserId,
                    ExpectedRevision = $"{config.CloudContainerAppName}--am-{workflowRun.Id}",
                    Config = config,
                    AzureCredentials = request.AzureCredentials
                });
            }
            else
            {
                await _workflowMonitor.StreamBuildLogAsync(deployment.Id, config.ProjectId,
                    "GitHub Actions workflow is still queued or running. Refresh the project details page for the latest persisted status.");
            }

            logger.LogInformation("Cloud deployment files committed.");

            return deployment;
        }
        catch (Exception ex)
        {
            try
            {
                await _workflowMonitor.StreamBuildLogAsync(deployment.Id, config.ProjectId,
                    "Cloud deployment preparation failed. Verify provider access and deployment configuration.");
            }
            catch (Exception diagnosticError)
            {
                logger.LogWarning("Could not publish cloud failure for deployment {DeploymentId}: {FailureType}.",
                    deployment.Id, diagnosticError.GetType().Name);
            }

            logger.LogError("Cloud deployment preparation failed: {FailureType}.", ex.GetType().Name);

            if (saasRun is null)
            {
                deployment.Status = DeploymentStatus.Failed;
                await dbContext.SaveChangesAsync(CancellationToken.None);
                statusNotifier.NotifyStatusChanged(config.ProjectId, deployment.Status);
            }

            throw;
        }
    }
}