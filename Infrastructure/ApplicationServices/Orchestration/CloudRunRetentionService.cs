using Domain.Enums;
using Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Infrastructure.ApplicationServices.Orchestration;

/// <summary>Removes transient webhook and outbox metadata after the diagnostic retention window.</summary>
public sealed class CloudRunRetentionService(
    IServiceScopeFactory scopeFactory,
    ILogger<CloudRunRetentionService> logger) : BackgroundService
{
    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromHours(1));
        do
        {
            try
            {
                var cutoff = DateTimeOffset.UtcNow.AddDays(-30);
                await using var scope = scopeFactory.CreateAsyncScope();
                var db = scope.ServiceProvider.GetRequiredService<AutoMateDbContext>();
                var deliveryIds = await db.CloudWebhookDeliveries.AsNoTracking()
                    .Where(item => item.CreatedAt < cutoff)
                    .OrderBy(item => item.CreatedAt).Take(1_000)
                    .Select(item => item.Id).ToListAsync(stoppingToken);
                if (deliveryIds.Count > 0)
                    await db.CloudWebhookDeliveries.Where(item => deliveryIds.Contains(item.Id))
                        .ExecuteDeleteAsync(stoppingToken);

                var outboxIds = await db.CloudRunOutbox.AsNoTracking()
                    .Where(item => db.CloudDeploymentRuns.Any(run => run.Id == item.RunId &&
                                                                     run.CompletedAt < cutoff &&
                                                                     (run.Phase == CloudRunPhase.Succeeded ||
                                                                      run.Phase == CloudRunPhase.Failed ||
                                                                      run.Phase == CloudRunPhase.TimedOut)))
                    .OrderBy(item => item.CreatedAt).Take(1_000)
                    .Select(item => item.Id).ToListAsync(stoppingToken);
                if (outboxIds.Count > 0)
                    await db.CloudRunOutbox.Where(item => outboxIds.Contains(item.Id))
                        .ExecuteDeleteAsync(stoppingToken);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                logger.LogWarning(exception, "Expired SaaS cloud control metadata cleanup failed.");
            }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}