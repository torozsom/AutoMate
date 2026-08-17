using Core.Enums;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Services.Orchestration;

/// <summary>
///     Processes queued deployment jobs in host-managed background scopes.
/// </summary>
public sealed class DeploymentJobWorker(
    IDeploymentJobQueue queue,
    IServiceScopeFactory scopeFactory,
    IDeploymentStatusNotifier statusNotifier,
    ILogger<DeploymentJobWorker> logger)
    : BackgroundService
{
    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await foreach (var job in queue.DequeueAllAsync(stoppingToken))
                await ProcessJobSafelyAsync(job, stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            logger.LogInformation("Deployment job worker stopped.");
        }
        catch (Exception ex)
        {
            logger.LogCritical(ex,
                "Deployment job worker stopped unexpectedly. Queued deployments will resume after the host restarts.");
        }
    }

    /// <summary>
    ///     Processes a single job while keeping job failures from stopping the hosted worker.
    /// </summary>
    /// <param name="job">The queued deployment job to process.</param>
    /// <param name="cancellationToken">Stops job execution during host shutdown.</param>
    private async Task ProcessJobSafelyAsync(DeploymentJob job, CancellationToken cancellationToken)
    {
        try
        {
            await ProcessJobAsync(job, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            logger.LogInformation("Deployment job worker is stopping.");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Deployment job {JobType} failed for project {ProjectId}.",
                job.GetType().Name, job.ProjectId);

            if (job is not StopLocalDeploymentJob)
                NotifyFailureStatus(job.ProjectId);
        }
    }

    /// <summary>
    ///     Publishes a failed deployment status without letting notification errors escape the worker.
    /// </summary>
    /// <param name="projectId">The project whose queued deployment failed.</param>
    private void NotifyFailureStatus(Guid projectId)
    {
        try
        {
            // Surface background deployment failures to Blazor subscribers even when the orchestrator fails early.
            statusNotifier.NotifyStatusChanged(projectId, DeploymentStatus.Failed);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to publish deployment failure for project {ProjectId}.", projectId);
        }
    }

    /// <summary>
    ///     Resolves scoped orchestrators and dispatches one deployment job.
    /// </summary>
    /// <param name="job">The queued deployment job.</param>
    /// <param name="cancellationToken">Stops job execution during host shutdown.</param>
    private async Task ProcessJobAsync(DeploymentJob job, CancellationToken cancellationToken)
    {
        using var scope = scopeFactory.CreateScope();

        switch (job)
        {
            case LocalDeploymentJob localJob:
                var localOrchestrator = scope.ServiceProvider.GetRequiredService<ILocalDeploymentOrchestrator>();
                await localOrchestrator.DeployLocalProjectAsync(localJob.Config, cancellationToken);
                break;

            case CloudDeploymentJob cloudJob:
                var cloudOrchestrator = scope.ServiceProvider.GetRequiredService<ICloudDeploymentOrchestrator>();
                await cloudOrchestrator.DeployCloudProjectAsync(cloudJob.Request, cancellationToken);
                break;

            case StopLocalDeploymentJob stopJob:
                var stopOrchestrator = scope.ServiceProvider.GetRequiredService<ILocalDeploymentOrchestrator>();
                await stopOrchestrator.StopDeploymentAsync(stopJob.ProjectId, stopJob.ProjectName,
                    stopJob.CsProjectPath, cancellationToken);
                break;

            default:
                logger.LogWarning("Ignoring unsupported deployment job type {JobType}.", job.GetType().FullName);
                break;
        }
    }
}