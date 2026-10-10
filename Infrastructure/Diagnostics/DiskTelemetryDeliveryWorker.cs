using Application.Abstractions.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Infrastructure.Diagnostics;

/// <summary>Delivery never holds a database transaction or falls back to a database payload queue.</summary>
public sealed class DiskTelemetryDeliveryWorker(
    DiskTelemetrySpool spool,
    IServiceScopeFactory scopes,
    IOptions<TelemetryStorageOptions> options,
    ILogger<DiskTelemetryDeliveryWorker> logger) : BackgroundService
{
    private readonly Dictionary<string, (int Failures, DateTimeOffset RetryAt)> _retries = new();

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(2));
        while (!stoppingToken.IsCancellationRequested)
        {
            await spool.PurgeExpiredAsync(stoppingToken);
            var progressed = false;
            foreach (var path in await spool.SegmentPathsAsync(stoppingToken))
            {
                if (_retries.TryGetValue(path, out var retry) && retry.RetryAt > DateTimeOffset.UtcNow) continue;
                try
                {
                    if (await DeliverAsync(path, stoppingToken))
                    {
                        progressed = true;
                        _retries.Remove(path);
                    }
                    else
                    {
                        ScheduleRetry(path);
                    }
                }
                catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
                {
                    ScheduleRetry(path);
                    logger.LogWarning("Disk telemetry delivery delayed: {FailureType}.", ex.GetType().Name);
                }
            }

            foreach (var path in _retries.Keys.Where(path => !File.Exists(path)).ToArray()) _retries.Remove(path);
            if (!progressed && !await timer.WaitForNextTickAsync(stoppingToken)) break;
        }
    }

    private void ScheduleRetry(string path)
    {
        var failures = _retries.TryGetValue(path, out var previous) ? Math.Min(previous.Failures + 1, 8) : 1;
        _retries[path] = (failures, DateTimeOffset.UtcNow.AddSeconds(Math.Min(300, Math.Pow(2, failures))));
        TelemetryStorageMetrics.Retries.Add(1);
    }

    /// <summary>Reclaims a segment only when all permitted events are queryable or explicitly recorded as lost.</summary>
    public async Task<bool> DeliverAsync(string path, CancellationToken token)
    {
        var events = spool.ReadSegment(path);
        var lost = new List<DeploymentLogEnvelope>();
        var complete = true;
        await using var scope = scopes.CreateAsyncScope();
        foreach (var group in events.GroupBy(e => (e.TenantId, e.Event.ProjectId)))
        {
            var policy = await scope.ServiceProvider.GetRequiredService<TelemetryProjectPolicyCache>()
                .GetAsync(group.Key.ProjectId, token);
            var allowed = policy?.UserId == group.Key.TenantId;
            var expired = group.Where(e =>
                e.StoredAt.AddHours(options.Value.BufferHours) <= DateTimeOffset.UtcNow || !allowed).ToArray();
            lost.AddRange(expired);
            var active = group.Except(expired).ToArray();
            if (active.Length == 0) continue;
            try
            {
                var logs = active.Where(e => e.Channel is not null).ToArray();
                var metrics = active.Where(e => e.Event.Metrics is { Count: > 0 }).ToArray();
                await scope.ServiceProvider.GetRequiredService<IDeploymentLogWriter>().WriteAsync(logs, token);
                await scope.ServiceProvider.GetRequiredService<IDeploymentMetricWriter>().WriteAsync(metrics, token);
                complete &= await scope.ServiceProvider.GetRequiredService<IDeploymentLogQuery>()
                                .ContainsAsync(logs, token) &&
                            await scope.ServiceProvider.GetRequiredService<IDeploymentMetricQuery>()
                                .ContainsAsync(metrics, token);
            }
            catch (Exception) when (!token.IsCancellationRequested)
            {
                complete = false;
            }
        }

        if (complete)
        {
            await spool.RemoveAsync(path, lost, token);
            if (lost.Count > 0) TelemetryStorageMetrics.Dropped.Add(lost.Count);
        }

        return complete;
    }
}