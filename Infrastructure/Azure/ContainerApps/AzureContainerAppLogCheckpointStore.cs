using Domain.Entities.Diagnostics;
using Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Infrastructure.Azure.ContainerApps;

/// <summary>
///     Persists Azure Container Apps log cursors without retaining log content.
/// </summary>
/// <param name="dbContext">
///     Application metadata context used for ownership, leases and persisted state.
/// </param>
internal sealed class AzureContainerAppLogCheckpointStore(AutoMateDbContext dbContext)
{
    /// <summary>
    ///     Loads or creates non-sensitive cursor metadata for one verified deployment log source.
    /// </summary>
    /// <param name="deploymentId">
    ///     The deployment identity; null is used only where legacy observations permit it.
    /// </param>
    /// <param name="source">Verified source classification, resource identity or owned input stream.</param>
    /// <param name="cancellationToken">Cancels the underlying operation when observed.</param>
    /// <returns>
    ///     A task returning the azure container app log checkpoint projection derived from the
    ///     verified inputs.
    /// </returns>
    public async Task<AzureContainerAppLogCheckpoint> GetOrCreateAsync(Guid deploymentId, string source,
        CancellationToken cancellationToken)
    {
        var checkpoint = await dbContext.AzureContainerAppLogCheckpoints
            .SingleOrDefaultAsync(item =>
                    item.DeploymentId == deploymentId
                    && item.Source == source,
                cancellationToken);

        if (checkpoint is not null) return checkpoint;

        checkpoint = new AzureContainerAppLogCheckpoint { DeploymentId = deploymentId, Source = source };
        dbContext.AzureContainerAppLogCheckpoints.Add(checkpoint);

        await dbContext.SaveChangesAsync(cancellationToken);
        return checkpoint;
    }

    /// <summary>
    ///     Persists tracked source cursor changes after durable event acknowledgement.
    /// </summary>
    /// <param name="cancellationToken">Cancels the underlying operation when observed.</param>
    /// <returns>A task representing completion of the asynchronous operation.</returns>
    public Task SaveAsync(CancellationToken cancellationToken)
    {
        return dbContext.SaveChangesAsync(cancellationToken);
    }
}
