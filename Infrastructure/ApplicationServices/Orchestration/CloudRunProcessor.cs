using System.Data;
using System.Diagnostics;
using System.Net;
using System.Text.Json;
using Application.Abstractions.GitHub;
using Application.Diagnostics;
using Application.Orchestration;
using Domain.DTO;
using Domain.Enums;
using Infrastructure.Azure;
using Infrastructure.Data;
using Infrastructure.GitHub;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;

namespace Infrastructure.ApplicationServices.Orchestration;

/// <summary>Claims SaaS launches with cluster-wide admission and executes a short launch phase.</summary>
public sealed class CloudRunProcessor(
    AutoMateDbContext dbContext,
    IGitHubAppCredentials githubApp,
    AzureArmCredentialsProvider azureCredentials,
    ICloudDeploymentOrchestrator orchestrator,
    IOptions<CloudSaasOptions> options,
    ILogger<CloudRunProcessor> logger)
{
    /// <summary>Atomic cluster-wide claim, including fair user ordering and provider limits.</summary>
    public async Task<(Guid RunId, Guid LeaseOwner)?> TryClaimAsync(CancellationToken cancellationToken)
    {
        await using var transaction = await dbContext.Database.BeginTransactionAsync(
            IsolationLevel.ReadCommitted, cancellationToken);
        await dbContext.Database.SqlQueryRaw<int>(
                "SELECT 1 AS \"Value\" FROM pg_advisory_xact_lock(617773318)")
            .SingleAsync(cancellationToken);
        var now = DateTimeOffset.UtcNow;
        var limits = options.Value;
        var active = await dbContext.CloudDeploymentRuns.CountAsync(item =>
            item.Phase == CloudRunPhase.Preparing && item.LeaseUntil > now, cancellationToken);
        if (active >= limits.MaxActiveGlobally) return null;

        // Filter blocked work before ordering. A fixed candidate window could otherwise starve
        // another user when many earlier rows belong to an installation at its limit.
        var id = await dbContext.Database.SqlQuery<Guid>($"""
                                                          SELECT r.id AS "Value" FROM cloud_deployment_runs r
                                                          WHERE ((r.phase = 'Queued' AND r.next_attempt_at <= {now})
                                                             OR (r.phase = 'Preparing' AND r.lease_until < {now}))
                                                            AND EXISTS (SELECT 1 FROM cloud_run_outbox o WHERE o.run_id = r.id)
                                                            AND (SELECT count(*) FROM cloud_deployment_runs a
                                                                 WHERE a.user_id = r.user_id AND a.phase = 'Preparing'
                                                                   AND a.lease_until > {now}) < {limits.MaxActivePerUser}
                                                            AND (SELECT count(*) FROM cloud_deployment_runs a
                                                                 WHERE a.installation_id = r.installation_id AND a.phase = 'Preparing'
                                                                   AND a.lease_until > {now}) < {limits.MaxActivePerInstallation}
                                                            AND NOT EXISTS (SELECT 1 FROM cloud_installation_budgets b
                                                                 WHERE b.installation_id = r.installation_id AND b.paused_until > {now})
                                                            AND NOT EXISTS (SELECT 1 FROM cloud_deployment_runs e
                                                                 WHERE (e.created_at, e.id) < (r.created_at, r.id)
                                                                   AND e.phase NOT IN ('Succeeded', 'Failed', 'TimedOut')
                                                                   AND (e.project_id = r.project_id OR
                                                                        (e.repository_id = r.repository_id AND e.branch_name = r.branch_name
                                                                         AND e.environment_name = r.environment_name)))
                                                          ORDER BY (SELECT MAX(h.launch_started_at) FROM cloud_deployment_runs h
                                                                    WHERE h.user_id = r.user_id) NULLS FIRST, r.created_at, r.id
                                                          LIMIT 1
                                                          """)
            .FirstOrDefaultAsync(cancellationToken);
        if (id != Guid.Empty)
        {
            var run = await dbContext.CloudDeploymentRuns.SingleAsync(item => item.Id == id, cancellationToken);
            var leaseOwner = Guid.NewGuid();
            var recovered = run.Phase == CloudRunPhase.Preparing;
            run.Phase = CloudRunPhase.Preparing;
            run.LeaseOwner = leaseOwner;
            run.LeaseUntil = now.AddMinutes(2);
            run.LaunchStartedAt = now;
            run.Attempt++;
            var outbox = await dbContext.CloudRunOutbox.SingleAsync(item => item.RunId == id, cancellationToken);
            outbox.DispatchedAt = now;
            await dbContext.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            AutoMateTelemetry.CloudLaunchesStarted.Add(1);
            if (recovered) AutoMateTelemetry.CloudLeasesRecovered.Add(1);
            if (run.Attempt == 1)
                AutoMateTelemetry.CloudQueueWait.Record((now - run.CreatedAt).TotalMilliseconds);
            return (id, leaseOwner);
        }

        return null;
    }

    /// <summary>Resumes the launch from its protected snapshot using fresh provider credentials.</summary>
    public async Task ProcessAsync(Guid runId, Guid leaseOwner, CancellationToken cancellationToken)
    {
        var launchTimer = Stopwatch.StartNew();
        var run = await dbContext.CloudDeploymentRuns.SingleAsync(item => item.Id == runId, cancellationToken);
        if (run.LeaseOwner != leaseOwner || run.Phase != CloudRunPhase.Preparing) return;
        try
        {
            var snapshot = JsonSerializer.Deserialize<CloudRunSnapshot>(run.SnapshotJson
                                                                        ?? throw new InvalidOperationException(
                                                                            "The deployment snapshot is missing."))
                           ?? throw new InvalidOperationException("The deployment snapshot is invalid.");
            var githubToken = await githubApp.CreateInstallationTokenAsync(run.InstallationId, cancellationToken);
            var azure = await azureCredentials.GetAsync(run.UserId, cancellationToken);
            var request = new CloudDeploymentRequestDto
            {
                SaasRunId = run.Id,
                SaasLeaseOwner = leaseOwner,
                DeferWorkflowMonitoring = true,
                RequestingUserId = run.UserId,
                Config = snapshot.Config,
                Metadata = snapshot.Metadata,
                CsProjectName = snapshot.CsProjectName,
                RepositoryRoot = snapshot.RepositoryRoot,
                GitHubAccessToken = githubToken,
                AzureCredentials = azure,
                RepositoryOwner = run.RepositoryOwner,
                RepositoryName = run.RepositoryName,
                BranchName = run.BranchName,
                WorkflowFileName = run.WorkflowFileName
            };
            await orchestrator.DeployCloudProjectAsync(request, cancellationToken);
            AutoMateTelemetry.CloudLaunchDuration.Record(launchTimer.Elapsed.TotalMilliseconds);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // The lease expires and another worker safely resumes after shutdown.
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning("Cloud launch {RunId} attempt {Attempt} failed ({FailureType}).",
                runId, run.Attempt, ex.GetType().Name);
            dbContext.ChangeTracker.Clear();
            run = await dbContext.CloudDeploymentRuns.SingleAsync(item => item.Id == runId, cancellationToken);
            if (run.LeaseOwner != leaseOwner) return;
            run.LeaseOwner = null;
            run.LeaseUntil = null;
            var throttled = ex is GitHubRateLimitException ||
                            ex.GetType().Name.Contains("RateLimit", StringComparison.OrdinalIgnoreCase);
            var authorizationDenied = ex is HttpRequestException
            {
                StatusCode: HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden
            };
            var retryAt = ex is GitHubRateLimitException rate
                ? rate.RetryAt
                : DateTimeOffset.UtcNow.AddSeconds(Math.Min(300, 10 * (1 << Math.Min(run.Attempt, 4))) +
                                                   Random.Shared.Next(0, 10));
            if (throttled)
            {
                AutoMateTelemetry.CloudProviderThrottles.Add(1);
                await dbContext.Database.ExecuteSqlInterpolatedAsync($"""
                                                                      INSERT INTO cloud_installation_budgets
                                                                          (installation_id, paused_until, updated_at, throttle_count)
                                                                      VALUES ({run.InstallationId}, {retryAt}, {DateTimeOffset.UtcNow}, 1)
                                                                      ON CONFLICT (installation_id) DO UPDATE SET
                                                                          paused_until = GREATEST(cloud_installation_budgets.paused_until, EXCLUDED.paused_until),
                                                                          updated_at = EXCLUDED.updated_at,
                                                                          throttle_count = cloud_installation_budgets.throttle_count + 1
                                                                      """, cancellationToken);
            }

            if (run.Attempt < 5 && !authorizationDenied &&
                (ex is HttpRequestException or DbUpdateException or NpgsqlException or TimeoutException ||
                 throttled))
            {
                run.Phase = CloudRunPhase.Queued;
                run.NextAttemptAt = retryAt;
                var outbox = await dbContext.CloudRunOutbox.SingleAsync(item => item.RunId == run.Id,
                    cancellationToken);
                outbox.DispatchedAt = null;
            }
            else
            {
                run.Phase = CloudRunPhase.Failed;
                run.FailureReason = SafeFailureReason(ex);
                run.CompletedAt = DateTimeOffset.UtcNow;
                run.SnapshotJson = null;
                if (run.DeploymentId is { } deploymentId)
                {
                    var deployment = await dbContext.Deployments.SingleAsync(item => item.Id == deploymentId,
                        cancellationToken);
                    deployment.Status = DeploymentStatus.Failed;
                }
            }

            await dbContext.SaveChangesAsync(cancellationToken);
            if (run.Phase == CloudRunPhase.Failed)
                AutoMateTelemetry.CloudRunsFailed.Add(1);
        }
    }

    /// <summary>Allows only authored, non-provider error text into the customer-visible run row.</summary>
    private static string SafeFailureReason(Exception exception)
    {
        if (exception is UnauthorizedAccessException)
            return "GitHub authorization failed. Reconnect GitHub and verify repository access.";
        if (exception is HttpRequestException
            {
                StatusCode: HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden
            })
            return "Provider authorization failed. Reconnect the affected account and verify its permissions.";
        if (exception is InvalidOperationException)
        {
            if (exception.Message.StartsWith("Reconnect Azure", StringComparison.Ordinal) ||
                exception.Message.StartsWith("Azure authorization expired", StringComparison.Ordinal))
                return "Reconnect Azure and verify deployment permissions before retrying.";
            if (exception.Message.StartsWith("The Azure Container Registry name", StringComparison.Ordinal))
                return "Verify the Azure Container Registry name and retry.";
            if (exception.Message.StartsWith("The AutoMate GitHub App installation", StringComparison.Ordinal))
                return "Verify the AutoMate GitHub App installation and repository access before retrying.";
            if (exception.Message.StartsWith("Unable to verify the prior AutoMate commit", StringComparison.Ordinal))
                return "The prior AutoMate commit could not be verified. Verify GitHub access before retrying.";
        }

        return "Cloud deployment preparation failed. Review the deployment logs.";
    }
}