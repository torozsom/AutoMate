using Application.Abstractions.Ai;
using Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Infrastructure.Ai;

public sealed class DeploymentAnalysisQueue(AutoMateDbContext dbContext) : IDeploymentAnalysisQueue
{
    public async Task<DeploymentAnalysisWorkItem?> ClaimNextAsync(CancellationToken cancellationToken = default)
    {
        var work = await dbContext.DeploymentAnalysisWorkItems.OrderBy(item => item.CreatedAt)
            .FirstOrDefaultAsync(item => item.CompletedAt == null && item.ClaimedAt == null, cancellationToken);
        if (work is null) return null;
        work.ClaimedAt = DateTimeOffset.UtcNow;
        await dbContext.SaveChangesAsync(cancellationToken);
        return new(work.AnalysisId, Guid.Empty);
    }
}
