using System.Diagnostics;
using System.Text.RegularExpressions;
using Application.Abstractions.Hosting;
using Application.Diagnostics;
using Domain.Enums;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Application.Orchestration;

/// <summary>Admits independent deployment jobs into bounded local and cloud lanes.</summary>
public sealed class DeploymentJobWorker(
    IDeploymentJobQueue queue,
    IServiceScopeFactory scopeFactory,
    IDeploymentStatusNotifier statusNotifier,
    IDeploymentCapabilities capabilities,
    IOptions<DeploymentConcurrencyOptions> options,
    ILogger<DeploymentJobWorker> logger) : BackgroundService
{
    private readonly int _cloudLimit = Math.Clamp(options.Value.MaxCloudDeployments, 1, 16);

    private readonly int _localLimit = options.Value.MaxLocalBuilds switch
    {
        -1 => Math.Max(2, Environment.ProcessorCount / 2),
        0 => int.MaxValue,
        var configured => Math.Clamp(configured, 1, 1_024)
    };

    private readonly int _pendingLimit = Math.Clamp(options.Value.MaxQueuedJobs, 1, 1_000);

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var pending = new List<QueuedDeploymentJob>();
        var running = new List<RunningJob>();
        using var readCancellation = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        await using var reader =
            queue.DequeueAllAsync(readCancellation.Token).GetAsyncEnumerator(readCancellation.Token);
        Task<bool>? readTask = null;

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                AdmitReadyJobs(pending, running, stoppingToken);
                if (readTask is null && pending.Count < _pendingLimit)
                    readTask = reader.MoveNextAsync().AsTask();

                var waits = running.Select(item => (Task)item.Task).ToList();
                if (readTask is not null) waits.Add(readTask);
                if (waits.Count == 0) break;

                var completed = await Task.WhenAny(waits);
                if (completed == readTask)
                {
                    if (!await readTask!) break;
                    pending.Add(reader.Current);
                    readTask = null;
                    continue;
                }

                var finished = running.First(item => item.Task == completed);
                running.Remove(finished);
                await FinishAsync(finished);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            logger.LogInformation("Deployment job worker stopped.");
        }
        catch (Exception ex)
        {
            logger.LogCritical(ex, "Deployment job scheduler stopped unexpectedly: {FailureType}.", ex.GetType().Name);
        }
        finally
        {
            // Async iterators cannot be disposed while MoveNextAsync is still pending.
            await readCancellation.CancelAsync();
            if (readTask is not null)
                try
                {
                    await readTask;
                }
                catch (OperationCanceledException) when (readCancellation.IsCancellationRequested)
                {
                }
                catch (Exception ex)
                {
                    logger.LogCritical(ex, "Deployment job scheduler stopped unexpectedly: {FailureType}.",
                        ex.GetType().Name);
                }

            // Every admitted job owns a scope and must finish before the hosted worker exits.
            foreach (var item in running)
                await FinishAsync(item);
        }
    }

    private void AdmitReadyJobs(List<QueuedDeploymentJob> pending, List<RunningJob> running,
        CancellationToken cancellationToken)
    {
        var earlierProjects = new HashSet<Guid>();
        for (var index = 0; index < pending.Count;)
        {
            var queued = pending[index];
            var job = queued.Job;
            if (!earlierProjects.Add(job.ProjectId) || running.Any(item => item.Queued.Job.ProjectId == job.ProjectId)
                                                    || !LaneAvailable(job, running) || ResourceInUse(job, running))
            {
                index++;
                continue;
            }

            pending.RemoveAt(index);
            queue.MarkStarted(job);
            var waitMs = Stopwatch.GetElapsedTime(queued.EnqueuedTimestamp).TotalMilliseconds;
            AutoMateTelemetry.DeploymentQueueWait.Record(waitMs, new KeyValuePair<string, object?>("lane", Lane(job)));
            AutoMateTelemetry.DeploymentJobsStarted.Add(1, new KeyValuePair<string, object?>("lane", Lane(job)));
            logger.LogInformation("Starting {JobType} for project {ProjectId} after {WaitMs:F0} ms queued.",
                job.GetType().Name, job.ProjectId, waitMs);
            running.Add(new RunningJob(queued, ProcessJobSafelyAsync(job, cancellationToken)));
        }
    }

    private bool LaneAvailable(DeploymentJob job, List<RunningJob> running)
    {
        return job switch
        {
            LocalDeploymentJob => running.Count(item => item.Queued.Job is LocalDeploymentJob) < _localLimit,
            CloudDeploymentJob => running.Count(item => item.Queued.Job is CloudDeploymentJob) < _cloudLimit,
            StopLocalDeploymentJob => running.Count(item => item.Queued.Job is StopLocalDeploymentJob) < 4,
            _ => false
        };
    }

    private static bool ResourceInUse(DeploymentJob job, List<RunningJob> running)
    {
        if (job is CloudDeploymentJob cloud && running.Any(item =>
                item.Queued.Job is CloudDeploymentJob other &&
                string.Equals(other.Request.RepositoryOwner, cloud.Request.RepositoryOwner,
                    StringComparison.OrdinalIgnoreCase) &&
                string.Equals(other.Request.RepositoryName, cloud.Request.RepositoryName,
                    StringComparison.OrdinalIgnoreCase) &&
                string.Equals(other.Request.BranchName, cloud.Request.BranchName,
                    StringComparison.OrdinalIgnoreCase)))
            return true;
        var composeName = ComposeName(job);
        if (composeName is not null && running.Any(item => ComposeName(item.Queued.Job) == composeName))
            return true;
        return job is LocalDeploymentJob local && running.Any(item =>
            item.Queued.Job is LocalDeploymentJob other && other.Config.ExposedPort == local.Config.ExposedPort);
    }

    private static string? ComposeName(DeploymentJob job)
    {
        var name = job switch
        {
            LocalDeploymentJob local => local.Config.ProjectName,
            StopLocalDeploymentJob stop => stop.ProjectName,
            _ => null
        };
        if (name is null) return null;
        var normalized = Regex.Replace(name.Trim().ToLowerInvariant(), "[^a-z0-9_-]+", "-");
        normalized = string.Join('-', normalized.Split('-', StringSplitOptions.RemoveEmptyEntries));
        return string.IsNullOrEmpty(normalized) ? "automate-project" : normalized;
    }

    private static string Lane(DeploymentJob job)
    {
        return job switch
        {
            LocalDeploymentJob => "local",
            CloudDeploymentJob => "cloud",
            StopLocalDeploymentJob => "stop",
            _ => "unknown"
        };
    }

    private async Task FinishAsync(RunningJob running)
    {
        var succeeded = await running.Task;
        queue.MarkCompleted(running.Queued.Job);
        logger.LogInformation("Finished {JobType} for project {ProjectId} with outcome {Outcome}.",
            running.Queued.Job.GetType().Name, running.Queued.Job.ProjectId,
            succeeded ? AuditOutcome.Completed : AuditOutcome.Failed);
        var lane = Lane(running.Queued.Job);
        if (succeeded)
            AutoMateTelemetry.DeploymentJobsCompleted.Add(1,
                new KeyValuePair<string, object?>("lane", lane));
        else
            AutoMateTelemetry.DeploymentJobsFailed.Add(1,
                new KeyValuePair<string, object?>("lane", lane));
    }

    /// <summary>Isolates job failures and passes exceptions to the host's redacted console diagnostics boundary.</summary>
    private async Task<bool> ProcessJobSafelyAsync(DeploymentJob job, CancellationToken cancellationToken)
    {
        try
        {
            await ProcessJobAsync(job, cancellationToken);
            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            logger.LogInformation("Deployment job {JobType} was cancelled for project {ProjectId}.",
                job.GetType().Name, job.ProjectId);
            return false;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Deployment job {JobType} failed for project {ProjectId}: {FailureType}.",
                job.GetType().Name, job.ProjectId, ex.GetType().Name);
            if (job is not StopLocalDeploymentJob) NotifyFailureStatus(job.ProjectId);
            return false;
        }
    }

    /// <summary>Publishes safe status changes while isolating and diagnosing subscriber failures.</summary>
    private void NotifyFailureStatus(Guid projectId)
    {
        try
        {
            statusNotifier.NotifyStatusChanged(projectId, DeploymentStatus.Failed);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to publish deployment failure for project {ProjectId}: {FailureType}.",
                projectId, ex.GetType().Name);
        }
    }

    private async Task ProcessJobAsync(DeploymentJob job, CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        switch (job)
        {
            case LocalDeploymentJob localJob:
                if (!capabilities.LocalDeploymentsEnabled)
                    throw new InvalidOperationException(
                        "Local Docker deployments are disabled for this AutoMate instance.");
                await scope.ServiceProvider.GetRequiredService<ILocalDeploymentOrchestrator>()
                    .DeployLocalProjectAsync(localJob.Config, cancellationToken);
                break;
            case CloudDeploymentJob cloudJob:
                if (!capabilities.CloudDeploymentsEnabled)
                    throw new InvalidOperationException("Cloud deployments are disabled for this AutoMate instance.");
                await scope.ServiceProvider.GetRequiredService<ICloudDeploymentOrchestrator>()
                    .DeployCloudProjectAsync(cloudJob.Request, cancellationToken);
                break;
            case StopLocalDeploymentJob stopJob:
                if (!capabilities.LocalDeploymentsEnabled)
                    throw new InvalidOperationException(
                        "Local Docker deployments are disabled for this AutoMate instance.");
                await scope.ServiceProvider.GetRequiredService<ILocalDeploymentOrchestrator>()
                    .StopDeploymentAsync(stopJob.ProjectId, stopJob.ProjectName, stopJob.CsProjectPath,
                        cancellationToken);
                break;
            default:
                throw new NotSupportedException($"Unsupported deployment job type {job.GetType().Name}.");
        }
    }

    private sealed record RunningJob(QueuedDeploymentJob Queued, Task<bool> Task);
}