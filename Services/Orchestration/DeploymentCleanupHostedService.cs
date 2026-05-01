using Core.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Services.Data;

namespace Services.Orchestration;

/// <summary>
///     A hosted service that runs at application startup to clean up
///     any deployments that are stuck in "Building" or "Starting" status.
/// </summary>
public class DeploymentCleanupHostedService(
    IServiceProvider serviceProvider,
    ILogger<DeploymentCleanupHostedService> logger)
    : IHostedService
{
    private const string SystemFailureLog
        = "\n[System]: Deployment marked as failed due to application restart or timeout.";


    /// <summary>
    ///     This method is called when the application starts. It initiates the cleanup process by running the
    ///     CleanupStuckDeploymentsAsync method in a separate task. This allows the cleanup to run asynchronously
    ///     without blocking the application startup process.
    /// </summary>
    /// <param name="cancellationToken">
    ///     A cancellation token that can be used to cancel the operation if needed.
    /// </param>
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        logger.LogInformation("[DeploymentCleanupHostedService] Starting to clean up stuck deployments...");
        _ = Task.Run(async () => await CleanupStuckDeploymentsAsync(cancellationToken), cancellationToken);
    }


    /// This method is called when the application is stopping. Now it simply returns a completed task.
    public Task StopAsync(CancellationToken cancellationToken)
    {
        logger.LogInformation("DeploymentCleanupHostedService is stopping.");
        return Task.CompletedTask;
    }


    /// <summary>
    ///     This method performs a bulk update on the Deployments table to set the status of any deployments
    ///     that are currently in "Building" or "Starting" status to "Failed". It also appends a system log message
    ///     to indicate that the deployment was marked as failed due to application restart or timeout.
    /// </summary>
    /// <param name="cancellationToken">
    ///     A cancellation token that can be used to cancel the operation if needed. This allows the method to
    ///     respond to application shutdown signals and stop the cleanup process gracefully if the application
    ///     is stopping while the cleanup is still in progress.
    /// </param>
    private async Task CleanupStuckDeploymentsAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var scope = serviceProvider.CreateScope();
            var dbContext = scope.ServiceProvider.GetRequiredService<AutoMateDbContext>();

            var updatedCount = await dbContext.Deployments
                .Where(d => d.Status == DeploymentStatus.Building || d.Status == DeploymentStatus.Starting)
                .ExecuteUpdateAsync(setters => setters
                        .SetProperty(d => d.Status, DeploymentStatus.Failed),
                    cancellationToken);

            if (updatedCount > 0)
                logger.LogWarning("[DeploymentCleanupHostedService] Successfully cleaned up and marked {Count} " +
                                  "stuck deployments as 'Failed'.", updatedCount);
            else
                logger.LogInformation(
                    "[DeploymentCleanupHostedService] No stuck deployments found. Database is clean.");
        }
        catch (Exception ex)
        {
            logger.LogCritical(ex, "[DeploymentCleanupHostedService] CRITICAL: Error occurred while " +
                                   "executing bulk update to clean up stuck deployments.");
        }
    }
}