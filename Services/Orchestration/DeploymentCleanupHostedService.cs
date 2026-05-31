using Core.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Services.Data;
using Services.Docker;

namespace Services.Orchestration;

/// <summary>
///     A hosted service that runs at application startup to clean up
///     any deployments that are stuck in "Building" or "Starting" status,
///     and synchronize the status of "Running" or "Stopped" deployments with the actual Docker daemon state.
/// </summary>
public class DeploymentCleanupHostedService(
    IServiceProvider serviceProvider,
    ILogger<DeploymentCleanupHostedService> logger)
    : IHostedService
{
    /// <summary>
    ///     This method is called when the application starts. It initiates the cleanup process by running the
    ///     CleanupStuckDeploymentsAsync method in a separate task. This allows the cleanup to run asynchronously
    ///     without blocking the application startup process.
    /// </summary>
    /// <param name="cancellationToken">
    ///     A cancellation token that can be used to cancel the operation if needed.
    /// </param>
    public Task StartAsync(CancellationToken cancellationToken)
    {
        logger.LogInformation("[DeploymentCleanupHostedService] Starting to clean up and sync deployments...");
        _ = Task.Run(async () => await CleanupStuckDeploymentsAsync(cancellationToken), cancellationToken);
        return Task.CompletedTask;
    }


    /// This method is called when the application is stopping. Now it simply returns a completed task.
    public Task StopAsync(CancellationToken cancellationToken)
    {
        logger.LogInformation("DeploymentCleanupHostedService is stopping.");
        return Task.CompletedTask;
    }


    /// <summary>
    ///     This method synchronizes the status of deployments in the database with the actual Docker state.
    ///     It marks "Starting" or "Building" deployments as "Failed".
    ///     It also checks "Running" deployments and marks them as "Stopped" if their Docker Compose project is not running.
    /// </summary>
    /// <param name="cancellationToken">
    ///     A cancellation token that can be used to cancel the operation if needed.
    /// </param>
    private async Task CleanupStuckDeploymentsAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var scope = serviceProvider.CreateScope();
            var dbContext = scope.ServiceProvider.GetRequiredService<AutoMateDbContext>();
            var dockerService = scope.ServiceProvider.GetRequiredService<IDockerService>();

            // 1. Mark 'Starting' as 'Failed'
            var updatedCount = await dbContext.Deployments
                .Where(d => d.Status == DeploymentStatus.Starting)
                .ExecuteUpdateAsync(setters => setters
                        .SetProperty(d => d.Status, DeploymentStatus.Failed),
                    cancellationToken);

            if (updatedCount > 0)
                logger.LogWarning("[DeploymentCleanupHostedService] Successfully cleaned up and marked {Count} " +
                                  "stuck deployments (Starting) as 'Failed'.", updatedCount);

            // 2. Synchronize 'Running' and 'Stopped' deployments with Docker daemon
            var runningDeployments = await dbContext.Deployments
                .Include(d => d.CsProject)
                .ThenInclude(csp => csp!.Application)
                .Where(d => d.Status == DeploymentStatus.Running || d.Status == DeploymentStatus.Stopped)
                .ToListAsync(cancellationToken);

            if (runningDeployments.Count > 0)
            {
                var runningProjectsInDocker = await dockerService.GetRunningProjectNamesAsync();
                var changedCount = 0;

                foreach (var deployment in runningDeployments)
                {
                    if (deployment.CsProject == null) continue;

                    var expectedProjectName = NormalizeComposeProjectName(
                        deployment.CsProject.Application?.Name ?? deployment.CsProject.Name);
                    var isActuallyRunning =
                        runningProjectsInDocker.Contains(expectedProjectName, StringComparer.OrdinalIgnoreCase);

                    if (deployment.Status == DeploymentStatus.Running && !isActuallyRunning)
                    {
                        deployment.Status = DeploymentStatus.Stopped;
                        changedCount++;
                    }
                    else if (deployment.Status == DeploymentStatus.Stopped && isActuallyRunning)
                    {
                        deployment.Status = DeploymentStatus.Running;
                        changedCount++;
                    }
                }

                if (changedCount > 0)
                {
                    await dbContext.SaveChangesAsync(cancellationToken);
                    logger.LogWarning("[DeploymentCleanupHostedService] Synchronized {Count} deployments with " +
                                      "the actual Docker state.", changedCount);
                }
            }

            logger.LogInformation("[DeploymentCleanupHostedService] Deployment sync completed.");
        }
        catch (OperationCanceledException)
        {
            logger.LogInformation("[DeploymentCleanupHostedService] Deployment cleanup was cancelled.");
        }
        catch (Exception ex)
        {
            logger.LogCritical(ex, "[DeploymentCleanupHostedService] CRITICAL: Error occurred while " +
                                   "executing bulk update to clean up and sync deployments.");
        }
    }


    /// <summary>
    ///     Normalizes a string to be used as a Docker Compose project name by converting to lowercase,
    ///     replacing invalid characters with hyphens, and collapsing multiple hyphens into a single one.
    /// </summary>
    /// <param name="value">The string to normalize.</param>
    /// <returns>The normalized Docker Compose project name.</returns>
    private static string NormalizeComposeProjectName(string value)
    {
        var normalized = new string(value
            .Trim()
            .ToLowerInvariant()
            .Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' ? c : '-')
            .ToArray());

        normalized = string.Join('-', normalized.Split('-',
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

        return string.IsNullOrWhiteSpace(normalized) ? "automate-project" : normalized;
    }
}