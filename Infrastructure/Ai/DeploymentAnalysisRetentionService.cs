using Application.Diagnostics;
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
    internal static Task<int> DeleteBatchAsync(AutoMateDbContext db, DateTimeOffset now, CancellationToken token)
    {
        return db.AiDeploymentAnalyses.Where(item => item.ExpiresAt <= now)
            .OrderBy(item => item.ExpiresAt).ThenBy(item => item.Id).Take(BatchSize).ExecuteDeleteAsync(token);
    }

    /// <summary>Deletes one bounded receipt expiry batch; current-day quota and live idempotency receipts remain intact.</summary>
    internal static Task<int> DeleteRequestBatchAsync(AutoMateDbContext db, DateTimeOffset now, CancellationToken token)
    {
        return db.AiAnalysisRequests.Where(item => item.ExpiresAt <= now)
            .OrderBy(item => item.ExpiresAt).ThenBy(item => item.Id).Take(BatchSize).ExecuteDeleteAsync(token);
    }
}