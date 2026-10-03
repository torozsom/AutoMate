using Application.Diagnostics;
using Domain.Enums;
using Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Infrastructure.ApplicationServices.Orchestration;

/// <summary>Samples cluster-wide queue and monitor state for autoscaling and alerts.</summary>
public sealed class CloudRunMetricsService(IServiceScopeFactory scopeFactory,
    ILogger<CloudRunMetricsService> logger) : BackgroundService
{
    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(30));
        do
        {
            try
            {
                await using var scope = scopeFactory.CreateAsyncScope();
                var db = scope.ServiceProvider.GetRequiredService<AutoMateDbContext>();
                var now = DateTimeOffset.UtcNow;
                var queued = await db.CloudDeploymentRuns.CountAsync(item =>
                    item.Phase == CloudRunPhase.Queued, stoppingToken);
                var active = await db.CloudDeploymentRuns.CountAsync(item =>
                    item.Phase == CloudRunPhase.Preparing && item.LeaseUntil > now, stoppingToken);
                var webhookBacklog = await db.CloudWebhookDeliveries.CountAsync(item =>
                    item.ProcessedAt == null, stoppingToken);
                var reconciliationBacklog = await db.CloudDeploymentRuns.CountAsync(item =>
                    (item.Phase == CloudRunPhase.AwaitingWorkflow ||
                     item.Phase == CloudRunPhase.WorkflowRunning) &&
                    item.UpdatedAt < now.AddMinutes(-2), stoppingToken);
                var oldest = await db.CloudDeploymentRuns.AsNoTracking()
                    .Where(item => item.Phase == CloudRunPhase.Queued)
                    .OrderBy(item => item.CreatedAt).Select(item => (DateTimeOffset?)item.CreatedAt)
                    .FirstOrDefaultAsync(stoppingToken);
                var ageMs = oldest is null ? 0 : Math.Max(0, (long)(now - oldest.Value).TotalMilliseconds);
                AutoMateTelemetry.SetCloudControlPlaneSnapshot(queued, active, webhookBacklog,
                    reconciliationBacklog, ageMs);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                logger.LogWarning(exception, "SaaS cloud control metrics could not be sampled.");
            }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
