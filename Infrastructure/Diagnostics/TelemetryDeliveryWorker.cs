using Application.Abstractions.Diagnostics;
using Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Infrastructure.Diagnostics;

/// <summary>Leases tenant streams and retains each ordered batch until its complete prefix is query-visible.</summary>
public sealed class TelemetryDeliveryWorker(
    IServiceScopeFactory scopes,
    IOptions<TelemetryStorageOptions> options,
    ILogger<TelemetryDeliveryWorker> logger) : BackgroundService
{
    /// <summary>Bounds operational alerts while retaining measurements each cycle.</summary>
    private DateTimeOffset _nextCleanupWarning;

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Keep draining legacy database outboxes during migration, even with DiskGateway enabled.
        if (!options.Value.Specialized) return;
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(2));
        do
        {
            try
            {
                await DeliverOnceAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogWarning("Telemetry delivery cycle unavailable: {FailureType}.", ex.GetType().Name);
            }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    /// <summary>Processes at most eight eligible owners fairly in one bounded cycle.</summary>
    internal async Task DeliverOnceAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AutoMateDbContext>();
        var now = DateTimeOffset.UtcNow;
        var tenants = await db.TelemetryTenantStates.AsNoTracking()
            .Where(s =>
                (s.DueAt <= now ||
                 db.DeploymentDiagnosticRecords.Any(r => r.TenantId == s.TenantId && r.BufferExpiresAt <= now)) &&
                (s.LeaseUntil == null || s.LeaseUntil <= now) &&
                db.DeploymentDiagnosticRecords.Any(r => r.TenantId == s.TenantId && r.DeliveryJson != null))
            .OrderBy(s => s.DueAt).Take(8).Select(s => s.TenantId).ToListAsync(cancellationToken);
        TelemetryStorageMetrics.SpoolBytes.Record(await db.TelemetryTenantStates.Where(s => s.TenantId == Guid.Empty)
            .Select(s => s.BufferedBytes).SingleOrDefaultAsync(cancellationToken));
        var oldestDeadline = await db.DeploymentDiagnosticRecords.Where(r => r.DeliveryJson != null)
            .MinAsync(r => r.BufferExpiresAt, cancellationToken);
        var overdueSeconds = oldestDeadline is null ? 0 : Math.Max(0, (now - oldestDeadline.Value).TotalSeconds);
        TelemetryStorageMetrics.CleanupOverdueSeconds.Record(overdueSeconds);
        if (overdueSeconds > 600 && now >= _nextCleanupWarning)
        {
            logger.LogWarning(
                "Telemetry spool cleanup is overdue by {OverdueSeconds}s; operator attention is required.",
                overdueSeconds);
            _nextCleanupWarning = now.AddMinutes(5);
        }

        foreach (var tenant in tenants)
        {
            var lease = Guid.NewGuid();
            var claimed = await db.TelemetryTenantStates.Where(s => s.TenantId == tenant &&
                                                                    (s.LeaseUntil == null || s.LeaseUntil <= now))
                .ExecuteUpdateAsync(u => u.SetProperty(s => s.LeaseId, lease)
                    .SetProperty(s => s.LeaseUntil, now.AddMinutes(2)), cancellationToken);
            if (claimed == 0) continue;
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(TimeSpan.FromSeconds(45));
            try
            {
                var rows = await db.DeploymentDiagnosticRecords.AsNoTracking()
                    .Where(r => r.TenantId == tenant && r.DeliveryJson != null)
                    .OrderBy(r => r.OrderId).Take(options.Value.BatchSize).ToListAsync(deadline.Token);
                var allowedProjects = await db.Applications.Where(p => p.UserId == tenant &&
                                                                       (!options.Value.ManagedService ||
                                                                        p.ManagedTelemetryConsent)).Select(p => p.Id)
                    .ToListAsync(deadline.Token);
                var revoked = rows.Where(r => !allowedProjects.Contains(r.ProjectId)).ToArray();
                if (revoked.Length > 0)
                {
                    await DropAsync(db, tenant, lease, revoked.Select(r => r.Id).ToArray(), deadline.Token);
                    continue;
                }

                var expired = rows.TakeWhile(r => r.BufferExpiresAt <= DateTimeOffset.UtcNow).ToArray();
                if (expired.Length > 0)
                {
                    await DropAsync(db, tenant, lease, expired.Select(r => r.Id).ToArray(), deadline.Token);
                    continue;
                }

                var envelopes = rows.Select(DeploymentTelemetryStore.Envelope).ToArray();
                var logs = envelopes.Where(e => e.Channel is not null).ToArray();
                var metrics = envelopes.Where(e => e.Event.Metrics is { Count: > 0 }).ToArray();
                var logWriter = scope.ServiceProvider.GetRequiredService<IDeploymentLogWriter>();
                var metricWriter = scope.ServiceProvider.GetRequiredService<IDeploymentMetricWriter>();
                if (rows.Any(r => !r.DeliveryAccepted))
                {
                    await logWriter.WriteAsync(logs, deadline.Token);
                    await metricWriter.WriteAsync(metrics, deadline.Token);
                    if (!await OwnsLeaseAsync(db, tenant, lease, deadline.Token)) continue;
                    var ids = rows.Select(r => r.Id).ToArray();
                    await db.DeploymentDiagnosticRecords.Where(r => ids.Contains(r.Id))
                        .ExecuteUpdateAsync(u => u.SetProperty(r => r.DeliveryAccepted, true), deadline.Token);
                }

                var visible = await scope.ServiceProvider.GetRequiredService<IDeploymentLogQuery>()
                                  .ContainsAsync(logs, deadline.Token) &&
                              await scope.ServiceProvider.GetRequiredService<IDeploymentMetricQuery>()
                                  .ContainsAsync(metrics, deadline.Token);
                if (visible && await OwnsLeaseAsync(db, tenant, lease, deadline.Token))
                {
                    // Delete only the complete earliest batch, never a visible suffix that could hide delayed lines.
                    await RemoveBufferedAsync(db, tenant, rows.Select(r => r.Id).ToArray(), deadline.Token);
                    foreach (var row in rows)
                        TelemetryStorageMetrics.VisibilitySeconds.Record((DateTimeOffset.UtcNow - row.StoredAt!.Value)
                            .TotalSeconds);
                }

                await db.TelemetryTenantStates.Where(s => s.TenantId == tenant && s.LeaseId == lease)
                    .ExecuteUpdateAsync(
                        u => u.SetProperty(s => s.Attempts, 0)
                            .SetProperty(s => s.DueAt, DateTimeOffset.UtcNow.AddSeconds(2)), deadline.Token);
            }
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
            {
                var state = await db.TelemetryTenantStates.AsNoTracking()
                    .SingleAsync(s => s.TenantId == tenant, cancellationToken);
                var seconds = Math.Min(300, Math.Pow(2, Math.Min(state.Attempts + 1, 8))) +
                              Random.Shared.NextDouble() * 3;
                if (ex is TelemetryProviderException { RetryAfter: { } retry })
                    seconds = Math.Max(seconds, retry.TotalSeconds);
                seconds = Math.Min(seconds, 86400);
                await db.TelemetryTenantStates.Where(s => s.TenantId == tenant && s.LeaseId == lease)
                    .ExecuteUpdateAsync(u => u.SetProperty(s => s.Attempts, s => s.Attempts + 1)
                        .SetProperty(s => s.DueAt, DateTimeOffset.UtcNow.AddSeconds(seconds)), cancellationToken);
                TelemetryStorageMetrics.Retries.Add(1);
                logger.LogWarning("Telemetry owner {TenantId} delivery delayed by {DelaySeconds}s: {FailureType}.",
                    tenant, seconds, ex.GetType().Name);
            }
            finally
            {
                if (!cancellationToken.IsCancellationRequested)
                    await db.TelemetryTenantStates.Where(s => s.TenantId == tenant && s.LeaseId == lease)
                        .ExecuteUpdateAsync(u => u.SetProperty(s => s.LeaseId, (Guid?)null)
                            .SetProperty(s => s.LeaseUntil, (DateTimeOffset?)null), cancellationToken);
            }
        }
    }

    /// <summary>Checks fencing before mutating delivery state.</summary>
    private static Task<bool> OwnsLeaseAsync(AutoMateDbContext db, Guid tenant, Guid lease, CancellationToken token)
    {
        return db.TelemetryTenantStates.AnyAsync(
            s => s.TenantId == tenant && s.LeaseId == lease && s.LeaseUntil > DateTimeOffset.UtcNow, token);
    }

    /// <summary>Records bounded-buffer expiry as durable, visible loss before deleting payloads.</summary>
    private async Task DropAsync(AutoMateDbContext db, Guid tenant, Guid lease, Guid[] ids, CancellationToken token)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(token);
        if (db.Database.IsNpgsql())
            await db.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock(73104021)", token);
        if (!await OwnsLeaseAsync(db, tenant, lease, token)) return;
        await db.TelemetryTenantStates.Where(s => s.TenantId == tenant && s.LeaseId == lease)
            .ExecuteUpdateAsync(u => u.SetProperty(s => s.DroppedEvents, s => s.DroppedEvents + ids.Length), token);
        await RemoveRowsAsync(db, tenant, ids, token);
        await transaction.CommitAsync(token);
        TelemetryStorageMetrics.Dropped.Add(ids.Length);
        logger.LogWarning("Expired {Count} buffered telemetry events for {TenantId}; history is incomplete.",
            ids.Length, tenant);
    }

    /// <summary>Removes confirmed payloads and releases capacity in the same short transaction.</summary>
    internal static async Task RemoveBufferedAsync(AutoMateDbContext db, Guid tenant, Guid[] ids,
        CancellationToken token)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(token);
        if (db.Database.IsNpgsql())
            await db.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock(73104021)", token);
        await RemoveRowsAsync(db, tenant, ids, token);
        await transaction.CommitAsync(token);
    }

    /// <summary>Accounts for actual deleted rows, making recovery and duplicate deletion harmless.</summary>
    private static async Task RemoveRowsAsync(AutoMateDbContext db, Guid tenant, Guid[] ids, CancellationToken token)
    {
        var bytes = await db.DeploymentDiagnosticRecords.Where(r => ids.Contains(r.Id) && r.DeliveryJson != null)
            .SumAsync(r => (long)r.DeliveryBytes, token);
        await db.TelemetryTenantStates.Where(s => s.TenantId == tenant || s.TenantId == Guid.Empty)
            .ExecuteUpdateAsync(u => u.SetProperty(s => s.BufferedBytes, s => Math.Max(0, s.BufferedBytes - bytes)),
                token);
        await db.DeploymentDiagnosticRecords.Where(r => ids.Contains(r.Id)).ExecuteDeleteAsync(token);
    }
}