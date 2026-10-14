using System.Text.Json;
using Application.Abstractions.Azure;
using Application.Abstractions.Diagnostics.Events;
using Application.Abstractions.Diagnostics.Redaction;
using Application.Abstractions.GitHub;
using Application.Diagnostics;
using Application.Orchestration.Cloud;
using Application.Orchestration.Status;
using Domain.DTO.Azure;
using Domain.DTO.Deployment;
using Domain.DTO.GitHub;
using Domain.Entities.Cloud;
using Domain.Enums.Deployments;
using Infrastructure.ApplicationServices.Orchestration.Lifecycle;
using Infrastructure.Azure.Identity;
using Infrastructure.Data;
using Infrastructure.GitHub.Api;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Infrastructure.ApplicationServices.Orchestration.CloudRuns;

/// <summary>
///     Applies verified workflow events and reconciles runs without occupying launch slots.
/// </summary>
/// <param name="dbContext">
///     Application metadata context used for ownership, leases and persisted state.
/// </param>
/// <param name="githubApp">Provides the git hub app credentials boundary used by this operation.</param>
/// <param name="github">Provides the git hub service boundary used by this operation.</param>
/// <param name="diagnostics">Accepts normalized observations for redaction and durable delivery.</param>
/// <param name="redactor">Applies shared credential and unsafe-text masking.</param>
/// <param name="monitoringOptions">
///     Provides the configured limits and endpoint settings for this service.
/// </param>
/// <param name="azureStreamer">
///     Provides the azure container app runtime streamer boundary used by this operation.
/// </param>
/// <param name="azureCredentials">
///     Resolves current scoped Azure authorization without logging credentials.
/// </param>
/// <param name="statusNotifier">Publishes deployment transitions to in-process subscribers.</param>
/// <param name="loggerFactory">
///     Receives reviewed operational events without diagnostic payloads or credentials.
/// </param>
public sealed class CloudRunMonitor(
    AutoMateDbContext dbContext,
    IGitHubAppCredentials githubApp,
    IGitHubService github,
    IDeploymentDiagnosticPublisher diagnostics,
    IDiagnosticRedactor redactor,
    IOptions<GitHubWorkflowMonitoringOptions> monitoringOptions,
    IAzureContainerAppRuntimeStreamer azureStreamer,
    AzureArmCredentialsProvider azureCredentials,
    IDeploymentStatusNotifier statusNotifier,
    ILoggerFactory loggerFactory)
{
    /// <summary>Samples deadlines and transitions at their original decision points.</summary>
    private readonly TimeProvider _clock = TimeProvider.System;

    /// <summary>
    ///     Uses an explicit execution clock while retaining the original constructor and system-clock defaults.
    /// </summary>
    /// <param name="dbContext">
    ///     Application metadata context used for ownership, leases and persisted state.
    /// </param>
    /// <param name="githubApp">Provides the git hub app credentials boundary used by this operation.</param>
    /// <param name="github">Provides the git hub service boundary used by this operation.</param>
    /// <param name="diagnostics">Accepts normalized observations for redaction and durable delivery.</param>
    /// <param name="redactor">Applies shared credential and unsafe-text masking.</param>
    /// <param name="monitoringOptions">
    ///     Provides the configured limits and endpoint settings for this service.
    /// </param>
    /// <param name="azureStreamer">
    ///     Provides the azure container app runtime streamer boundary used by this operation.
    /// </param>
    /// <param name="azureCredentials">
    ///     Resolves current scoped Azure authorization without logging credentials.
    /// </param>
    /// <param name="statusNotifier">Publishes deployment transitions to in-process subscribers.</param>
    /// <param name="loggerFactory">
    ///     Receives reviewed operational events without diagnostic payloads or credentials.
    /// </param>
    /// <param name="clock">The execution clock for the existing lease, retry and completion decisions.</param>
    public CloudRunMonitor(AutoMateDbContext dbContext,
        IGitHubAppCredentials githubApp,
        IGitHubService github,
        IDeploymentDiagnosticPublisher diagnostics,
        IDiagnosticRedactor redactor,
        IOptions<GitHubWorkflowMonitoringOptions> monitoringOptions,
        IAzureContainerAppRuntimeStreamer azureStreamer,
        AzureArmCredentialsProvider azureCredentials,
        IDeploymentStatusNotifier statusNotifier,
        ILoggerFactory loggerFactory,
        TimeProvider clock)
        : this(dbContext, githubApp, github, diagnostics, redactor, monitoringOptions, azureStreamer, azureCredentials,
            statusNotifier, loggerFactory)
    {
        _clock = clock;
    }

    /// <summary>
    ///     Collects normalized GitHub job diagnostics using the existing redacted pipeline.
    /// </summary>
    /// <returns>The git hub workflow monitor projection derived from the verified inputs.</returns>
    private GitHubWorkflowMonitor CreateWorkflowMonitor()
    {
        return new GitHubWorkflowMonitor(dbContext, github, diagnostics, redactor,
            monitoringOptions.Value, loggerFactory.CreateLogger<GitHubWorkflowMonitor>());
    }

    /// <summary>
    ///     Claims and processes one verified webhook receipt.
    /// </summary>
    /// <param name="cancellationToken">Cancels the underlying operation when observed.</param>
    /// <returns>A task returning whether the documented process next delivery async condition holds.</returns>
    public async Task<bool> ProcessNextDeliveryAsync(CancellationToken cancellationToken)
    {
        var now = _clock.GetUtcNow();
        var delivery = await dbContext.CloudWebhookDeliveries
            .Where(item => item.ProcessedAt == null && (item.LeaseUntil == null || item.LeaseUntil < now))
            .OrderBy(item => item.CreatedAt).FirstOrDefaultAsync(cancellationToken);
        if (delivery is null) return false;
        var owner = Guid.NewGuid();
        var claimed = await dbContext.CloudWebhookDeliveries.Where(item => item.Id == delivery.Id &&
                                                                           item.ProcessedAt == null &&
                                                                           (item.LeaseUntil == null ||
                                                                            item.LeaseUntil < now))
            .ExecuteUpdateAsync(setters => setters.SetProperty(item => item.LeaseOwner, owner)
                .SetProperty(item => item.LeaseUntil, now.AddMinutes(10)), cancellationToken);
        if (claimed == 0) return true;
        dbContext.Entry(delivery).State = EntityState.Detached;
        delivery = await dbContext.CloudWebhookDeliveries.SingleAsync(item => item.Id == delivery.Id,
            cancellationToken);
        try
        {
            var run = await dbContext.CloudDeploymentRuns.SingleOrDefaultAsync(item =>
                    item.InstallationId == delivery.InstallationId && item.RepositoryId == delivery.RepositoryId &&
                    item.CommitSha == delivery.HeadSha && item.BranchName == delivery.HeadBranch,
                cancellationToken);
            if (run is null)
            {
                if (delivery.CreatedAt > now.AddMinutes(-2)) return false;
            }
            else if (delivery.WorkflowPath.EndsWith("/" + run.WorkflowFileName,
                         StringComparison.OrdinalIgnoreCase) &&
                     (run.WorkflowRunId is null || run.WorkflowRunId == delivery.WorkflowRunId))
            {
                if (await dbContext.CloudInstallationBudgets.AnyAsync(item =>
                            item.InstallationId == run.InstallationId && item.PausedUntil > now,
                        cancellationToken)) return false;
                var runLeaseOwner = Guid.NewGuid();
                var acquired = await dbContext.CloudDeploymentRuns.Where(item => item.Id == run.Id &&
                        (item.LeaseUntil == null || item.LeaseUntil < now))
                    .ExecuteUpdateAsync(setters => setters.SetProperty(item => item.LeaseOwner, runLeaseOwner)
                        .SetProperty(item => item.LeaseUntil, now.AddMinutes(30)), cancellationToken);
                if (acquired == 0) return false;
                var workflow = new GitHubWorkflowRunDto
                {
                    Id = delivery.WorkflowRunId,
                    Attempt = delivery.WorkflowAttempt,
                    HeadSha = delivery.HeadSha,
                    HeadBranch = run.BranchName,
                    Status = delivery.Status,
                    Conclusion = delivery.Conclusion,
                    HtmlUrl =
                        $"https://github.com/{run.RepositoryOwner}/{run.RepositoryName}/actions/runs/{delivery.WorkflowRunId}"
                };
                try
                {
                    await ApplyWorkflowAsync(run, workflow, cancellationToken);
                }
                catch (GitHubRateLimitException limit)
                {
                    await PauseInstallationAsync(run.InstallationId, limit.RetryAt, cancellationToken);
                    return false;
                }
                finally
                {
                    await dbContext.CloudDeploymentRuns.Where(item => item.Id == run.Id &&
                                                                      item.LeaseOwner == runLeaseOwner)
                        .ExecuteUpdateAsync(setters => setters.SetProperty(item => item.LeaseOwner, (Guid?)null)
                            .SetProperty(item => item.LeaseUntil, (DateTimeOffset?)null), cancellationToken);
                }
            }

            delivery.ProcessedAt = _clock.GetUtcNow();
            AutoMateTelemetry.CloudWebhookLag.Record((delivery.ProcessedAt.Value - delivery.CreatedAt)
                .TotalMilliseconds);
            delivery.LeaseOwner = null;
            delivery.LeaseUntil = null;
            await dbContext.SaveChangesAsync(cancellationToken);
            return true;
        }
        finally
        {
            if (delivery.ProcessedAt is null)
            {
                dbContext.ChangeTracker.Clear();
                await dbContext.CloudWebhookDeliveries.Where(item => item.Id == delivery.Id &&
                                                                     item.LeaseOwner == owner)
                    .ExecuteUpdateAsync(setters => setters.SetProperty(item => item.LeaseOwner, (Guid?)null)
                        .SetProperty(item => item.LeaseUntil,
                            _clock.GetUtcNow().AddSeconds(15)), cancellationToken);
            }
        }
    }

    /// <summary>
    ///     Shares a provider cooldown with every worker and monitor replica.
    /// </summary>
    /// <param name="installationId">The verified GitHub App installation identity.</param>
    /// <param name="retryAt">UTC time when the next bounded attempt becomes eligible.</param>
    /// <param name="cancellationToken">Cancels the underlying operation when observed.</param>
    /// <returns>A task representing completion of the asynchronous operation.</returns>
    private async Task PauseInstallationAsync(long installationId, DateTimeOffset retryAt,
        CancellationToken cancellationToken)
    {
        AutoMateTelemetry.CloudProviderThrottles.Add(1);
        await dbContext.Database.ExecuteSqlInterpolatedAsync($"""
                                                              INSERT INTO cloud_installation_budgets
                                                                  (installation_id, paused_until, updated_at, throttle_count)
                                                              VALUES ({installationId}, {retryAt}, {_clock.GetUtcNow()}, 1)
                                                              ON CONFLICT (installation_id) DO UPDATE SET
                                                                  paused_until = GREATEST(cloud_installation_budgets.paused_until, EXCLUDED.paused_until),
                                                                  updated_at = EXCLUDED.updated_at,
                                                                  throttle_count = cloud_installation_budgets.throttle_count + 1
                                                              """, cancellationToken);
    }

    /// <summary>
    ///     Rechecks one stale run in case its webhook was missed or arrived early.
    /// </summary>
    /// <param name="cancellationToken">Cancels the underlying operation when observed.</param>
    /// <returns>A task returning whether the documented reconcile next async condition holds.</returns>
    public async Task<bool> ReconcileNextAsync(CancellationToken cancellationToken)
    {
        var now = _clock.GetUtcNow();
        var run = await dbContext.CloudDeploymentRuns
            .Where(item => (item.Phase == CloudRunPhase.AwaitingWorkflow ||
                            item.Phase == CloudRunPhase.WorkflowRunning) &&
                           item.UpdatedAt < now.AddMinutes(-2) && item.CommitSha != null &&
                           (item.LeaseUntil == null || item.LeaseUntil < now) &&
                           !dbContext.CloudInstallationBudgets.Any(budget =>
                               budget.InstallationId == item.InstallationId && budget.PausedUntil > now))
            .OrderBy(item => item.UpdatedAt).FirstOrDefaultAsync(cancellationToken);
        if (run is null) return false;
        var owner = Guid.NewGuid();
        var claimed = await dbContext.CloudDeploymentRuns.Where(item => item.Id == run.Id &&
                                                                        (item.LeaseUntil == null ||
                                                                         item.LeaseUntil < now))
            .ExecuteUpdateAsync(setters => setters.SetProperty(item => item.LeaseOwner, owner)
                .SetProperty(item => item.LeaseUntil, now.AddMinutes(30)), cancellationToken);
        if (claimed == 0) return true;
        try
        {
            var token = await githubApp.CreateInstallationTokenAsync(run.InstallationId, cancellationToken);
            var workflow = await github.GetLatestWorkflowRunAsync(token, run.RepositoryOwner,
                run.RepositoryName, run.WorkflowFileName, run.BranchName, run.CommitSha, cancellationToken);
            if (workflow is not null)
                await ApplyWorkflowAsync(run, workflow, cancellationToken);
            if (run.CommittedAt < now.AddMinutes(-60) &&
                run.Phase is CloudRunPhase.AwaitingWorkflow or CloudRunPhase.WorkflowRunning)
            {
                run.Phase = CloudRunPhase.TimedOut;
                run.FailureReason = "GitHub Actions did not complete within the 60-minute monitoring window.";
                run.CompletedAt = now;
                run.SnapshotJson = null;
                if (run.DeploymentId is { } deploymentId)
                {
                    var deployment = await dbContext.Deployments.SingleAsync(item => item.Id == deploymentId,
                        cancellationToken);
                    deployment.Status = DeploymentStatus.Failed;
                    statusNotifier.NotifyStatusChanged(run.ProjectId, DeploymentStatus.Failed);
                }
            }

            run.LeaseOwner = null;
            run.LeaseUntil = null;
            await dbContext.SaveChangesAsync(cancellationToken);
            if (run.Phase == CloudRunPhase.TimedOut)
                AutoMateTelemetry.CloudRunsTimedOut.Add(1);
            return true;
        }
        catch (GitHubRateLimitException limit)
        {
            await PauseInstallationAsync(run.InstallationId, limit.RetryAt, cancellationToken);
            return true;
        }
        finally
        {
            await dbContext.CloudDeploymentRuns.Where(item => item.Id == run.Id && item.LeaseOwner == owner)
                .ExecuteUpdateAsync(setters => setters.SetProperty(item => item.LeaseOwner, (Guid?)null)
                    .SetProperty(item => item.LeaseUntil, (DateTimeOffset?)null), cancellationToken);
        }
    }

    /// <summary>
    ///     Applies a workflow state transition and persists its redacted diagnostics.
    /// </summary>
    /// <param name="run">Verified cloud control record or provider workflow-run metadata.</param>
    /// <param name="workflow">Verified workflow metadata or its durable checkpoint.</param>
    /// <param name="cancellationToken">Cancels the underlying operation when observed.</param>
    /// <returns>A task representing completion of the asynchronous operation.</returns>
    private async Task ApplyWorkflowAsync(CloudDeploymentRun run, GitHubWorkflowRunDto workflow,
        CancellationToken cancellationToken)
    {
        if (run.Phase is CloudRunPhase.Succeeded or CloudRunPhase.Failed or CloudRunPhase.TimedOut) return;
        if (run.DeploymentId is not { } deploymentId) return;
        var deployment = await dbContext.Deployments.SingleAsync(item => item.Id == deploymentId,
            cancellationToken);
        var snapshot = JsonSerializer.Deserialize<CloudRunSnapshot>(run.SnapshotJson!)!;
        var token = await githubApp.CreateInstallationTokenAsync(run.InstallationId, cancellationToken);
        var request = new CloudDeploymentRequestDto
        {
            SaasRunId = run.Id,
            RequestingUserId = run.UserId,
            Config = snapshot.Config,
            Metadata = snapshot.Metadata,
            CsProjectName = snapshot.CsProjectName,
            RepositoryRoot = snapshot.RepositoryRoot,
            GitHubAccessToken = token,
            RepositoryOwner = run.RepositoryOwner,
            RepositoryName = run.RepositoryName,
            BranchName = run.BranchName,
            WorkflowFileName = run.WorkflowFileName
        };
        run.WorkflowRunId = workflow.Id;
        deployment.CloudGitHubActionRunId = workflow.Id;
        await CreateWorkflowMonitor().ObserveWorkflowRunAsync(request, deployment, workflow,
            cancellationToken);
        if (workflow.Status == "completed")
        {
            run.CompletedAt = _clock.GetUtcNow();
            run.Phase = workflow.Conclusion == "success" ? CloudRunPhase.Succeeded : CloudRunPhase.Failed;
            run.SnapshotJson = null;
            deployment.Status = workflow.Conclusion == "success" ? DeploymentStatus.Running : DeploymentStatus.Failed;
            deployment.Outcome = workflow.Conclusion == "success"
                ? DeploymentOutcome.Succeeded
                : DeploymentOutcome.Failed;
            deployment.FinishedAt = run.CompletedAt;
            if (workflow.Conclusion != "success")
                run.FailureReason = $"GitHub Actions concluded: {workflow.Conclusion ?? "unknown"}.";
            await dbContext.SaveChangesAsync(cancellationToken);
            if (run.Phase == CloudRunPhase.Succeeded)
                AutoMateTelemetry.CloudRunsSucceeded.Add(1);
            else
                AutoMateTelemetry.CloudRunsFailed.Add(1);
            statusNotifier.NotifyStatusChanged(run.ProjectId, deployment.Status);
            if (workflow.Conclusion == "success")
                try
                {
                    var azure = await azureCredentials.GetAsync(run.UserId, cancellationToken);
                    deployment.CloudContainerAppName = snapshot.Config.CloudContainerAppName;
                    deployment.CloudContainerRevision = $"{snapshot.Config.CloudContainerAppName}--am-{workflow.Id}";
                    deployment.CloudResourceId =
                        $"/subscriptions/{Uri.EscapeDataString(azure.SubscriptionId)}/resourceGroups/{Uri.EscapeDataString(snapshot.Config.CloudResourceGroupName)}/providers/Microsoft.App/containerApps/{Uri.EscapeDataString(snapshot.Config.CloudContainerAppName)}";
                    await dbContext.SaveChangesAsync(cancellationToken);
                    azureStreamer.StartStreaming(new AzureContainerAppRuntimeStreamRequest
                    {
                        ProjectId = run.ProjectId,
                        DeploymentId = deployment.Id,
                        UserId = run.UserId,
                        ExpectedRevision = $"{snapshot.Config.CloudContainerAppName}--am-{workflow.Id}",
                        Config = snapshot.Config,
                        AzureCredentials = azure
                    });
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    loggerFactory.CreateLogger<CloudRunMonitor>().LogWarning(
                        "Azure runtime streaming could not start for deployment {DeploymentId} ({FailureType}).",
                        deployment.Id, ex.GetType().Name);
                    await CreateWorkflowMonitor().StreamBuildLogAsync(deployment.Id, run.ProjectId,
                        "Azure runtime monitoring is temporarily unavailable; reconnect Azure to restore it.");
                }
        }
        else
        {
            // Delayed "requested" deliveries must not move a running workflow backwards.
            if (workflow.Status == "in_progress")
                run.Phase = CloudRunPhase.WorkflowRunning;
            await dbContext.SaveChangesAsync(cancellationToken);
        }
    }
}
