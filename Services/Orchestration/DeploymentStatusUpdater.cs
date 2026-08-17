using Core.Entities;
using Core.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Services.Data;

namespace Services.Orchestration;

/// <summary>
///     Persists deployment status transitions and notifies interested UI subscribers.
/// </summary>
internal sealed class DeploymentStatusUpdater(
    AutoMateDbContext dbContext,
    IDeploymentStatusNotifier statusNotifier,
    ILogger logger,
    string logSource)
{
    /// <summary>
    ///     Updates a deployment status and logs EF persistence failures without throwing.
    /// </summary>
    public async Task SafeUpdateAsync(Guid projectId, Deployment deployment, DeploymentStatus status,
        CancellationToken cancellationToken = default)
    {
        try
        {
            deployment.Status = status;
            await dbContext.SaveChangesAsync(cancellationToken);
            statusNotifier.NotifyStatusChanged(projectId, status);
        }
        catch (DbUpdateException ex)
        {
            logger.LogCritical(ex, "[{LogSource}] CRITICAL: Failed to update deployment status to '{Status}' " +
                                   "for Deployment ID {Id}.", logSource, status, deployment.Id);
        }
    }

    /// <summary>
    ///     Updates a deployment status and lets persistence errors propagate to the active workflow.
    /// </summary>
    public async Task UpdateAsync(Guid projectId, Deployment deployment, DeploymentStatus status,
        CancellationToken cancellationToken = default)
    {
        deployment.Status = status;
        await dbContext.SaveChangesAsync(cancellationToken);
        statusNotifier.NotifyStatusChanged(projectId, status);
    }
}