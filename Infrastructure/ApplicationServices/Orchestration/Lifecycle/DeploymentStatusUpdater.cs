using Domain.Entities;
using Domain.Enums;
using Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Application.Orchestration;

/// <summary>
///     Persists deployment status transitions and notifies interested UI subscribers.
/// </summary>
/// <param name="dbContext">
///     Application metadata context used for ownership, leases and persisted state.
/// </param>
/// <param name="statusNotifier">Publishes deployment transitions to in-process subscribers.</param>
/// <param name="logger">
///     Receives reviewed operational events without diagnostic payloads or credentials.
/// </param>
/// <param name="logSource">Finite Azure or Docker log source identifier.</param>
internal sealed class DeploymentStatusUpdater(
    AutoMateDbContext dbContext,
    IDeploymentStatusNotifier statusNotifier,
    ILogger logger,
    string logSource)
{
    /// <summary>
    ///     Updates a deployment status and logs EF persistence failures without throwing.
    /// </summary>
    /// <param name="projectId">The project identity used for authorization or correlation.</param>
    /// <param name="deployment">Tracked deployment metadata used for this exact launch or history scope.</param>
    /// <param name="status">Recorded runtime state or finite HTTP response status.</param>
    /// <param name="cancellationToken">Cancels the underlying operation when observed.</param>
    /// <returns>A task representing completion of the asynchronous operation.</returns>
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
    ///     Updates a deployment status and lets persistence errors propagate to the active
    ///     workflow.
    /// </summary>
    /// <param name="projectId">The project identity used for authorization or correlation.</param>
    /// <param name="deployment">Tracked deployment metadata used for this exact launch or history scope.</param>
    /// <param name="status">Recorded runtime state or finite HTTP response status.</param>
    /// <param name="cancellationToken">Cancels the underlying operation when observed.</param>
    /// <returns>A task representing completion of the asynchronous operation.</returns>
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