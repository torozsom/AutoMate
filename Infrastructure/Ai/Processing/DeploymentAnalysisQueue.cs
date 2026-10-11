using Application.Abstractions.Ai;
using Application.Ai;
using Application.Diagnostics;
using Domain.Enums;
using Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Infrastructure.Ai;

/// <summary>Uses conditional database updates for multi-instance ownership and renewable crash-recovery leases.</summary>
public sealed class DeploymentAnalysisQueue(
    AutoMateDbContext dbContext,
    TimeProvider clock,
    IOptionsMonitor<AiAnalysisOptions> options) : IDeploymentAnalysisQueue
{
    /// <inheritdoc />
    public async Task<DeploymentAnalysisWorkItem?> ClaimNextAsync(CancellationToken cancellationToken = default)
    {
        // Collisions retry a bounded selection window; no database lock spans context/provider work.
        for (var collision = 0; collision < 16; collision++)
        {
            var now = clock.GetUtcNow();
            var candidate = await dbContext.DeploymentAnalysisWorkItems.AsNoTracking()
                .Where(item => item.CompletedAt == null && (item.NextAttemptAt == null || item.NextAttemptAt <= now) &&
                               (item.LeaseUntil == null || item.LeaseUntil <= now) &&
                               item.Analysis.ExpiresAt > now &&
                               (item.Analysis.Status == AiAnalysisStatus.Queued ||
                                item.Analysis.Status == AiAnalysisStatus.Running))
                .OrderBy(item => item.CreatedAt).ThenBy(item => item.Id)
                .Select(item => new
                {
                    item.Id,
                    item.AnalysisId,
                    item.Analysis.DeploymentId,
                    item.AttemptCount,
                    item.ProviderRetryCount,
                    item.CreatedAt,
                    item.NextAttemptAt
                })
                .FirstOrDefaultAsync(cancellationToken);
            if (candidate is null) return null;
            var lease = Guid.NewGuid();
            var until = now.AddSeconds(Math.Clamp(options.CurrentValue.LeaseDurationSeconds, 30, 900));
            var attempt = Math.Clamp(candidate.AttemptCount, 0, 10) + 1;
            var changed = await dbContext.DeploymentAnalysisWorkItems.Where(item => item.Id == candidate.Id &&
                    item.CompletedAt == null && item.AttemptCount == candidate.AttemptCount &&
                    item.ProviderRetryCount == candidate.ProviderRetryCount &&
                    (item.NextAttemptAt == null || item.NextAttemptAt <= now) &&
                    (item.LeaseUntil == null || item.LeaseUntil <= now) && item.Analysis.ExpiresAt > now &&
                    (item.Analysis.Status == AiAnalysisStatus.Queued ||
                     item.Analysis.Status == AiAnalysisStatus.Running))
                .ExecuteUpdateAsync(update => update.SetProperty(item => item.LeaseId, lease)
                    .SetProperty(item => item.LeaseUntil, until).SetProperty(item => item.ClaimedAt, now)
                    .SetProperty(item => item.AttemptCount, attempt), cancellationToken);
            if (changed == 1)
            {
                AnalysisTelemetry.Claimed(candidate.NextAttemptAt ?? candidate.CreatedAt, clock.GetUtcNow());
                return new DeploymentAnalysisWorkItem(candidate.AnalysisId, candidate.DeploymentId,
                    lease, until, attempt, candidate.ProviderRetryCount);
            }
        }

        return null;
    }

    /// <inheritdoc />
    public async Task<bool> RenewAsync(DeploymentAnalysisWorkItem work, CancellationToken cancellationToken = default)
    {
        var now = clock.GetUtcNow();
        var until = now.AddSeconds(Math.Clamp(options.CurrentValue.LeaseDurationSeconds, 30, 900));
        return await dbContext.DeploymentAnalysisWorkItems.Where(item => item.AnalysisId == work.AnalysisId &&
                                                                         item.CompletedAt == null &&
                                                                         item.LeaseId == work.LeaseId &&
                                                                         item.LeaseUntil > now &&
                                                                         item.Analysis.ExpiresAt > now &&
                                                                         (item.Analysis.Status ==
                                                                          AiAnalysisStatus.Queued ||
                                                                          item.Analysis.Status ==
                                                                          AiAnalysisStatus.Running))
            .ExecuteUpdateAsync(
                update => update.SetProperty(item => item.LeaseUntil,
                    item => item.LeaseUntil > until ? item.LeaseUntil : until), cancellationToken) == 1;
    }

    /// <inheritdoc />
    public async Task<bool> ReleaseAsync(DeploymentAnalysisWorkItem work, CancellationToken cancellationToken = default)
    {
        var now = clock.GetUtcNow();
        return await dbContext.DeploymentAnalysisWorkItems.Where(item => item.AnalysisId == work.AnalysisId &&
                                                                         item.CompletedAt == null &&
                                                                         item.LeaseId == work.LeaseId &&
                                                                         item.LeaseUntil > now)
            .ExecuteUpdateAsync(update => update.SetProperty(item => item.LeaseId, (Guid?)null)
                .SetProperty(item => item.LeaseUntil, (DateTimeOffset?)null), cancellationToken) == 1;
    }
}