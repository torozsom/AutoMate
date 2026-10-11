using Domain.Entities;
using Domain.Enums;
using Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Application.Orchestration;

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
            logger.LogInformation(
                "Deployment status persisted for project {ProjectId}, deployment {DeploymentId}: {Status}.",
                projectId, deployment.Id, status);
            statusNotifier.NotifyStatusChanged(projectId, status);
        }
        catch (DbUpdateException ex)
        {
            logger.LogCritical(
                "[{LogSource}] Failed to update deployment status {Status} for Deployment ID {Id}. Failure {FailureType}.",
                logSource, status, deployment.Id, ex.GetType().Name);
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
        logger.LogInformation(
            "Deployment status persisted for project {ProjectId}, deployment {DeploymentId}: {Status}.",
            projectId, deployment.Id, status);
        statusNotifier.NotifyStatusChanged(projectId, status);
    }
}