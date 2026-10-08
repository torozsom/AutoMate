using Application.Diagnostics;
using Domain.Enums;
using Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Infrastructure.Ai;

/// <summary>Reclaims expired analysis metadata and cascading work items even when AI admission is disabled.</summary>
public sealed class DeploymentAnalysisRetentionService(
    IServiceScopeFactory scopes,
    TimeProvider clock,
    ILogger<DeploymentAnalysisRetentionService> logger)
    : BackgroundService
{
    /// <summary>Bounds each database delete to keep cleanup work predictable.</summary>
    internal const int BatchSize = 1_000;

    /// <inheritdoc />
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

    /// <summary>Deletes one bounded expiry batch; database cascades remove its queue rows.</summary>
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

    /// <summary>Deletes one bounded receipt expiry batch; current-day quota and live idempotency receipts remain intact.</summary>
    internal static Task<int> DeleteRequestBatchAsync(AutoMateDbContext db, DateTimeOffset now, CancellationToken token)
    {
        return db.AiAnalysisRequests.Where(item => item.ExpiresAt <= now)
            .OrderBy(item => item.ExpiresAt).ThenBy(item => item.Id).Take(BatchSize).ExecuteDeleteAsync(token);
    }

    /// <summary>Bounds cleanup of accounting metadata to ninety days, preserving all current-day/window charges.</summary>
    internal static Task<int> DeleteBudgetBatchAsync(AutoMateDbContext db, DateTimeOffset now, CancellationToken token)
    {
        return db.AiAnalysisBudgetEntries.Where(item => item.OccurredAt < now.AddDays(-90))
            .OrderBy(item => item.OccurredAt).ThenBy(item => item.Id).Take(BatchSize).ExecuteDeleteAsync(token);
    }
}