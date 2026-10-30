using Application.Abstractions.Diagnostics.Metrics;
using Infrastructure.Diagnostics.Metrics;
using Infrastructure.Diagnostics.Policy;
using Infrastructure.Diagnostics.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Infrastructure.Diagnostics.Delivery;

/// <summary>
///     Delivery never holds a database transaction or falls back to a database payload queue.
/// </summary>
/// <param name="spool">Private single-writer immutable segment store.</param>
/// <param name="scopes">Creates independent scopes for concurrent background work.</param>
/// <param name="options">Provides the configured limits and endpoint settings for this service.</param>
/// <param name="logger">
///     Receives reviewed operational events without diagnostic payloads or credentials.
/// </param>
public sealed class DiskTelemetryDeliveryWorker(
    DiskTelemetrySpool spool,
    IServiceScopeFactory scopes,
    IOptions<TelemetryStorageOptions> options,
    ILogger<DiskTelemetryDeliveryWorker> logger) : BackgroundService
{
    /// <summary>Per-segment bounded retry count and next eligible attempt time.</summary>
    private readonly Dictionary<string, (int Failures, DateTimeOffset RetryAt)> _retries = new();

    /// <summary>
    ///     Delivers pending immutable segments and confirms query visibility before removal.
    /// </summary>
    /// <param name="stoppingToken">Signals host shutdown.</param>
    /// <returns>A task representing the supervised service lifetime and cleanup.</returns>
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

    /// <summary>
    ///     Schedules bounded backoff for a segment whose durable delivery has not been confirmed.
    /// </summary>
    /// <param name="path">Filesystem or provider entry path used by this operation.</param>
    private void ScheduleRetry(string path)
    {
        var failures = _retries.TryGetValue(path, out var previous) ? Math.Min(previous.Failures + 1, 8) : 1;
        _retries[path] = (failures, DateTimeOffset.UtcNow.AddSeconds(Math.Min(300, Math.Pow(2, failures))));
        TelemetryStorageMetrics.Retries.Add(1);
    }

    /// <summary>
    ///     Reclaims a segment only when all permitted events are queryable or explicitly recorded
    ///     as lost.
    /// </summary>
    /// <param name="path">Filesystem or provider entry path used by this operation.</param>
    /// <param name="token">Cancels the underlying operation when observed.</param>
    /// <returns>A task returning whether the documented deliver async condition holds.</returns>
    public async Task<bool> DeliverAsync(string path, CancellationToken token)
    {
        var outcome = "failed";
        try
        {
            var complete = await DeliverCoreAsync(path, token);
            outcome = complete ? "completed" : "pending";

            return complete;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            outcome = "canceled";
            throw;
        }
        finally
        {
            TelemetryStorageMetrics.DeliveryAttempts.Add(1, new KeyValuePair<string, object?>("outcome", outcome));
        }
    }

    /// <summary>
    ///     Retains pending segments until every permitted event is query-visible.
    /// </summary>
    /// <param name="path">Verified segment path.</param>
    /// <param name="token">Cancels backend requests.</param>
    /// <returns>Whether the segment was retired.</returns>
    private async Task<bool> DeliverCoreAsync(string path, CancellationToken token)
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
            var expired = group
                .Where(e =>
                    e.StoredAt.AddHours(options.Value.BufferHours) <= DateTimeOffset.UtcNow || !allowed)
                .ToArray();

            lost.AddRange(expired);

            var active = group.Except(expired).ToArray();
            if (active.Length == 0) continue;

            try
            {
                var logs = active
                    .Where(e => e.Channel is not null)
                    .ToArray();

                var metrics = active
                    .Where(e => e.Event.Metrics is { Count: > 0 })
                    .ToArray();

                await scope.ServiceProvider.GetRequiredService<IDeploymentLogWriter>().WriteAsync(logs, token);
                await scope.ServiceProvider.GetRequiredService<IDeploymentMetricWriter>().WriteAsync(metrics, token);

                complete &= await scope.ServiceProvider
                                .GetRequiredService<IDeploymentLogQuery>()
                                .ContainsAsync(logs, token) &&
                            await scope.ServiceProvider
                                .GetRequiredService<IDeploymentMetricQuery>()
                                .ContainsAsync(metrics, token);
            }
            catch (Exception) when (!token.IsCancellationRequested)
            {
                complete = false;
            }
        }

        if (!complete) return complete;

        foreach (var item in events.Except(lost))
            TelemetryStorageMetrics.VisibilitySeconds.Record(Math.Max(0,
                (DateTimeOffset.UtcNow - item.StoredAt).TotalSeconds));
        await spool.RemoveAsync(path, lost, token);
        if (lost.Count > 0) TelemetryStorageMetrics.Dropped.Add(lost.Count);

        return complete;
    }
}
