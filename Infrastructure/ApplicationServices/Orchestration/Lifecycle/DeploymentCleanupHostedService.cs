using Application.Abstractions.Docker;
using Infrastructure.Data;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Application.Orchestration;

/// <summary>
///     A hosted service that runs at application startup to clean up any deployments that are
///     stuck in "Building" or "Starting" status, and synchronize the status of "Running" or
///     "Stopped" deployments with the actual Docker daemon state.
/// </summary>
/// <param name="serviceProvider">Resolves the current scoped operation dependencies.</param>
/// <param name="logger">
///     Receives reviewed operational events without diagnostic payloads or credentials.
/// </param>
public sealed class DeploymentCleanupHostedService(
    IServiceProvider serviceProvider,
    ILogger<DeploymentCleanupHostedService> logger)
    : IHostedService
{
    /// <summary>
    ///     This method is called when the application starts. It initiates the cleanup process by
    ///     running the CleanupStuckDeploymentsAsync method in a separate task. This allows the
    ///     cleanup to run asynchronously without blocking the application startup process.
    /// </summary>
    /// <param name="cancellationToken">
    ///     A cancellation token that can be used to cancel the operation if needed.
    /// </param>
    /// <returns>A task representing completion of the asynchronous operation.</returns>
    public Task StartAsync(CancellationToken cancellationToken)
    {
        logger.LogInformation("[DeploymentCleanupHostedService] Starting to clean up and sync deployments...");
        _ = Task.Run(async () => await CleanupStuckDeploymentsAsync(cancellationToken), cancellationToken);
        return Task.CompletedTask;
    }


    /// <summary>
    ///     Reports host shutdown without awaiting startup reconciliation, preserving the existing lifecycle contract.
    /// </summary>
    /// <param name="cancellationToken">Cancels the underlying operation when observed.</param>
    /// <returns>A task representing completion of the asynchronous operation.</returns>
    public Task StopAsync(CancellationToken cancellationToken)
    {
        logger.LogInformation("DeploymentCleanupHostedService is stopping.");
        return Task.CompletedTask;
    }


    /// <summary>
    ///     This method synchronizes the status of deployments in the database with the actual
    ///     Docker state. It marks "Starting" or "Building" deployments as "Failed". It also checks
    ///     "Running" deployments and marks them as "Stopped" if their Docker Compose project is not
    ///     running.
    /// </summary>
    /// <param name="cancellationToken">
    ///     A cancellation token that can be used to cancel the operation if needed.
    /// </param>
    /// <returns>A task representing completion of the asynchronous operation.</returns>
    private async Task CleanupStuckDeploymentsAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var scope = serviceProvider.CreateScope();
            var dbContext = scope.ServiceProvider.GetRequiredService<AutoMateDbContext>();
            var dockerService = scope.ServiceProvider.GetRequiredService<IDockerService>();

            await new DeploymentReconciliation(dbContext, dockerService, logger).RunAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            logger.LogInformation("[DeploymentCleanupHostedService] Deployment cleanup was cancelled.");
        }
        catch (Exception ex)
        {
            logger.LogCritical("Deployment cleanup and synchronization failed: {FailureType}.", ex.GetType().Name);
        }
    }
}
