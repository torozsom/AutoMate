using Application.Diagnostics;
using Domain.Enums.Ai;
using Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Infrastructure.Ai.Processing;

/// <summary>
///     Reclaims expired analysis metadata and cascading work items even when AI admission is
///     disabled.
/// </summary>
/// <param name="scopes">Creates independent scopes for concurrent background work.</param>
/// <param name="clock">Time source used for deterministic sampling and deadlines.</param>
/// <param name="logger">
///     Receives reviewed operational events without diagnostic payloads or credentials.
/// </param>
public sealed class DeploymentAnalysisRetentionService(
    IServiceScopeFactory scopes,
    TimeProvider clock,
    ILogger<DeploymentAnalysisRetentionService> logger)
    : BackgroundService
{
    /// <summary>Bounds each database delete to keep cleanup work predictable.</summary>
    internal const int BatchSize = 1_000;

    /// <summary>
    ///     Expires bounded analysis metadata while preserving saved results marked for retention.
    /// </summary>
    /// <param name="stoppingToken">Signals host shutdown.</param>
    /// <returns>A task representing the supervised service lifetime and cleanup.</returns>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromHours(1), clock);
        do
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                var db = scope.ServiceProvider.GetRequiredService<AutoMateDbContext>();
                var removed = 0;
                for (var batch = 0; batch < 10; batch++)
                {
                    var count = await DeleteBatchAsync(db, clock.GetUtcNow(), stoppingToken);
                    removed += count;
                    if (count < BatchSize) break;
                }

                for (var batch = 0; batch < 10; batch++)
                {
                    var count = await DeleteRequestBatchAsync(db, clock.GetUtcNow(), stoppingToken);
                    removed += count;
                    if (count < BatchSize) break;
                }

                for (var batch = 0; batch < 10; batch++)
                {
                    var count = await DeleteBudgetBatchAsync(db, clock.GetUtcNow(), stoppingToken);
                    removed += count;
                    if (count < BatchSize) break;
                }

                if (removed > 0)
                    OperationalLog.Record(logger, AuditOperation.AnalysisRetention, AuditOutcome.Completed);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                logger.LogError("Analysis retention cleanup failed: {FailureType}.", exception.GetType().Name);
            }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    /// <summary>
    ///     Deletes one bounded expiry batch; database cascades remove its queue rows.
    /// </summary>
    /// <param name="db">Application metadata context used for ownership, leases and persisted state.</param>
    /// <param name="now">The current time used for window validation or duration calculation.</param>
    /// <param name="token">Cancels the underlying operation when observed.</param>
    /// <returns>A task returning the numeric position, count or status described by this operation.</returns>
    internal static async Task<int> DeleteBatchAsync(AutoMateDbContext db, DateTimeOffset now, CancellationToken token)
    {
        var expiredWork = db.AiDeploymentAnalyses.Where(item => item.RetainUntilDeleted && item.ExpiresAt <= now &&
                                                                (item.Status == AiAnalysisStatus.Queued ||
                                                                 item.Status == AiAnalysisStatus.Running));
        await expiredWork.OrderBy(item => item.ExpiresAt).Take(BatchSize).ExecuteUpdateAsync(update => update
            .SetProperty(item => item.Status, AiAnalysisStatus.Cancelled)
            .SetProperty(item => item.CompletedAt, now), token);
        return await db.AiDeploymentAnalyses.Where(item => item.ExpiresAt <= now && !item.RetainUntilDeleted)
            .OrderBy(item => item.ExpiresAt).ThenBy(item => item.Id).Take(BatchSize).ExecuteDeleteAsync(token);
    }

    /// <summary>
    ///     Deletes one bounded receipt expiry batch; current-day quota and live idempotency
    ///     receipts remain intact.
    /// </summary>
    /// <param name="db">Application metadata context used for ownership, leases and persisted state.</param>
    /// <param name="now">The current time used for window validation or duration calculation.</param>
    /// <param name="token">Cancels the underlying operation when observed.</param>
    /// <returns>A task returning the numeric position, count or status described by this operation.</returns>
    internal static Task<int> DeleteRequestBatchAsync(AutoMateDbContext db, DateTimeOffset now, CancellationToken token)
    {
        return db.AiAnalysisRequests.Where(item => item.ExpiresAt <= now)
            .OrderBy(item => item.ExpiresAt).ThenBy(item => item.Id).Take(BatchSize).ExecuteDeleteAsync(token);
    }

    /// <summary>
    ///     Bounds cleanup of accounting metadata to ninety days, preserving all current-day/window
    ///     charges.
    /// </summary>
    /// <param name="db">Application metadata context used for ownership, leases and persisted state.</param>
    /// <param name="now">The current time used for window validation or duration calculation.</param>
    /// <param name="token">Cancels the underlying operation when observed.</param>
    /// <returns>A task returning the numeric position, count or status described by this operation.</returns>
    internal static Task<int> DeleteBudgetBatchAsync(AutoMateDbContext db, DateTimeOffset now, CancellationToken token)
    {
        return db.AiAnalysisBudgetEntries.Where(item => item.OccurredAt < now.AddDays(-90))
            .OrderBy(item => item.OccurredAt).ThenBy(item => item.Id).Take(BatchSize).ExecuteDeleteAsync(token);
    }
}
