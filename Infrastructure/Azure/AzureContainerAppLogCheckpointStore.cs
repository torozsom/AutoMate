using Domain.Entities;
using Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Infrastructure.Azure;

/// <summary>Persists Azure Container Apps log cursors without retaining log content.</summary>
internal sealed class AzureContainerAppLogCheckpointStore(AutoMateDbContext dbContext)
{
    public async Task<AzureContainerAppLogCheckpoint> GetOrCreateAsync(Guid deploymentId, string source,
        CancellationToken cancellationToken)
    {
        var checkpoint = await dbContext.AzureContainerAppLogCheckpoints.SingleOrDefaultAsync(item =>
            item.DeploymentId == deploymentId && item.Source == source, cancellationToken);
        if (checkpoint is not null) return checkpoint;

        checkpoint = new AzureContainerAppLogCheckpoint { DeploymentId = deploymentId, Source = source };
        dbContext.AzureContainerAppLogCheckpoints.Add(checkpoint);
        await dbContext.SaveChangesAsync(cancellationToken);
        return checkpoint;
    }

    public Task SaveAsync(CancellationToken cancellationToken) => dbContext.SaveChangesAsync(cancellationToken);
}
