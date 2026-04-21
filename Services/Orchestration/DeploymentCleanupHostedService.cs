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
    /// <summary>
    ///     Executes tasks at application startup to clean up any deployments stuck in "Building" or "Starting" status.
    /// </summary>
    /// <param name="cancellationToken">
    ///     A token to monitor for cancellation requests, passed from the host.
    /// </param>
    /// <returns>
    ///     A task that represents the asynchronous cleanup operation.
    /// </returns>
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        logger.LogInformation("Starting DeploymentCleanupHostedService to clean up stuck deployments...");

        using var scope = serviceProvider.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<AutoMateDbContext>();

        try
        {
            // Query the database for deployments that are stuck in "Building" or "Starting" status.
            var stuckDeployments = await dbContext.Deployments
                .Where(d => d.Status == DeploymentStatus.Building || d.Status == DeploymentStatus.Starting)
                .ToListAsync(cancellationToken);

            if (stuckDeployments.Count > 0)
            {
                logger.LogWarning("{Count} stuck deployments found. They will be set to 'Failed' status.",
                    stuckDeployments.Count);

                // Update the status of each stuck deployment to "Failed" and append a log message indicating the reason.
                foreach (var deployment in stuckDeployments)
                {
                    deployment.Status = DeploymentStatus.Failed;
                    deployment.Logs += "\n[System]: Deployment marked as failed due to timeout or unexpected error.";
                }

                await dbContext.SaveChangesAsync(cancellationToken);
            }
            else
            {
                logger.LogInformation("No stuck deployments found.");
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error occurred while cleaning up stuck deployments.");
        }
    }


    /// This method is called when the application is stopping. Now it simply returns a completed task.
    public Task StopAsync(CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }
}