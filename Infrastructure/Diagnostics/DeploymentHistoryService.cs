using System.Text.Json;
using Application.Abstractions.Diagnostics;
using Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Infrastructure.Diagnostics;

/// <summary>Authorizes deployment history before any provider query or preference change.</summary>
public sealed class DeploymentHistoryService(
    AutoMateDbContext db,
    DeploymentTelemetryStore store,
    IDeploymentMetricQuery metrics,
    IOptions<TelemetryStorageOptions> options,
    ITelemetryGateway? gateway = null) : IDeploymentHistoryService
{
    public async Task<TelemetryLogPage> ReadLogsV2Async(Guid user, Guid project, Guid deployment, string? cursor,
        bool backwards, int limit, string? search = null, CancellationToken token = default)
    {
        await AuthorizeAsync(user, project, deployment, token);
        if (search?.Length > 256) throw new ArgumentException("Log search is limited to 256 characters.");
        var history = await store.ReadPageAsync(project, deployment,
            TelemetryHistoryCursor.Decode(cursor, project, deployment),
            backwards, limit, token, search);
        return TelemetryHistoryCursor.Page(history, project, deployment);
    }

    /// <inheritdoc />
    public async Task<DeploymentTelemetryPreferences> GetPreferencesAsync(Guid userId, Guid projectId,
        CancellationToken cancellationToken = default)
    {
        var project = await db.Applications.AsNoTracking()
            .SingleOrDefaultAsync(p => p.Id == projectId && p.UserId == userId, cancellationToken);
        if (project is null) throw new UnauthorizedAccessException("Project access denied.");
        return new DeploymentTelemetryPreferences(project.RuntimeDiagnosticsEnabled, project.ManagedTelemetryConsent,
            options.Value.ManagedService, options.Value.ProcessingRegion);
    }

    /// <inheritdoc />
    public async Task SetManagedConsentAsync(Guid userId, Guid projectId, bool enabled,
        CancellationToken cancellationToken = default)
    {
        if (enabled && (!options.Value.ManagedService || !options.Value.ManagedDataProcessingApproved))
            throw new InvalidOperationException("Managed provider onboarding is not configured.");
        var changed = await db.Applications.Where(p => p.Id == projectId && p.UserId == userId)
            .ExecuteUpdateAsync(u => u.SetProperty(p => p.ManagedTelemetryConsent, enabled), cancellationToken);
        if (changed == 0) throw new UnauthorizedAccessException("Project access denied.");
    }

    /// <inheritdoc />
    public async Task<DeploymentTerminalHistory> ReadLogsAsync(Guid userId, Guid projectId, Guid deploymentId,
        long cursor, bool backwards, int limit, CancellationToken cancellationToken = default)
    {
        await AuthorizeAsync(userId, projectId, deploymentId, cancellationToken);
        return await store.ReadPageAsync(projectId, deploymentId, Math.Max(0, cursor), backwards, limit,
            cancellationToken);
    }

    /// <inheritdoc />
    public async Task<DeploymentMetricHistory> ReadMetricsAsync(Guid userId, Guid projectId, Guid deploymentId,
        DateTimeOffset start, DateTimeOffset end, int maximumPoints, CancellationToken cancellationToken = default)
    {
        await AuthorizeAsync(userId, projectId, deploymentId, cancellationToken);
        var now = DateTimeOffset.UtcNow;
        if (end <= start || end - start > TimeSpan.FromDays(30))
            throw new ArgumentException("Metric range must be positive and at most 30 days.");
        start = start < now.AddDays(-30) ? now.AddDays(-30) : start;
        end = end > now ? now : end;
        if (end <= start) return new DeploymentMetricHistory([], "Metric history has expired.");
        maximumPoints = Math.Clamp(maximumPoints, 1, 1000);
        IReadOnlyList<DeploymentMetricPoint> remote = [];
        string? availability = null;
        var specialized = options.Value.Specialized && (!options.Value.ManagedService ||
                                                        await db.Applications.AnyAsync(
                                                            p => p.Id == projectId && p.ManagedTelemetryConsent,
                                                            cancellationToken));
        if (options.Value.Specialized && !specialized)
            availability = "Managed storage requires owner consent; local metric history is shown.";
        if (specialized)
            try
            {
                remote = await metrics.ReadAsync(userId, projectId, deploymentId, start, end, maximumPoints,
                    cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException ||
                                       !cancellationToken.IsCancellationRequested)
            {
                availability = "Metric storage is temporarily unavailable; only buffered samples are shown.";
            }

        var rows = await db.DeploymentDiagnosticRecords.AsNoTracking().Where(r => r.ProjectId == projectId &&
                r.DeploymentId == deploymentId && r.ExpiresAt > now && r.TimestampUtc >= start &&
                r.TimestampUtc <= end &&
                r.MetricSamplesJson != null).OrderBy(r => r.TimestampUtc).Take(10001)
            .Select(r => new { r.TimestampUtc, r.MetricSamplesJson, r.SourceIdentityJson, r.DeliveryJson, r.Cursor })
            .ToListAsync(cancellationToken);
        var interval = Math.Max(60, (int)Math.Ceiling((end - start).TotalSeconds / maximumPoints));
        if (options.Value.DiskGateway)
            try
            {
                var pending = await gateway!.ReadPendingAsync(userId, projectId, deploymentId, cancellationToken);
                var buffered = pending.Events.Where(e => e.Event.TimestampUtc >= start && e.Event.TimestampUtc <= end)
                    .SelectMany(e => (e.Event.Metrics ?? []).Select(m => new
                    {
                        Sample = m,
                        Container = e.Event.TerminalChannel.Target ?? "unknown",
                        Time = end.AddSeconds(-Math.Floor((end - e.Event.TimestampUtc).TotalSeconds / interval) *
                                              interval)
                    }))
                    .GroupBy(p => (p.Container, p.Sample.Name, p.Sample.Unit, p.Time))
                    .Select(g => new DeploymentMetricPoint(g.Key.Container, g.Key.Name, g.Key.Unit, g.Key.Time,
                        g.Average(p => p.Sample.Value), g.Min(p => p.Sample.Value), g.Max(p => p.Sample.Value)))
                    .ToArray();
                remote = buffered.Concat(remote).DistinctBy(p => (p.Container, p.Name, p.Timestamp)).ToArray();
                if (buffered.Length > 0)
                    availability = "Recent metric intervals are pending ingestion and may be incomplete.";
                if (pending.DroppedEvents > 0 || pending.Truncated)
                    availability = "Some diagnostics were omitted or pending history exceeds the read limit.";
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                availability = "Pending metric storage is unavailable; confirmed samples are shown.";
            }

        if (rows.Count > 10000)
        {
            rows.RemoveAt(rows.Count - 1);
            availability = (availability + " Earlier sample processing limit reached; narrow the selected time range.")
                .Trim();
        }

        if (specialized && rows.Any(r => r.DeliveryJson != null))
            availability =
                (availability + " Recent metric intervals are pending ingestion and may be incomplete.").Trim();
        var local = rows.SelectMany(r => JsonSerializer.Deserialize<List<DeploymentMetricSample>>(r.MetricSamplesJson!)!
                .Select(m => new
                {
                    Sample = m,
                    Time = end.AddSeconds(-Math.Floor((end - r.TimestampUtc).TotalSeconds / interval) * interval),
                    Container = r.DeliveryJson is not null
                        ? JsonSerializer
                            .Deserialize<DeploymentDiagnosticEvent>(r.DeliveryJson, TelemetryHttpTransport.Json)
                            ?.TerminalChannel.Target ?? "unknown"
                        : r.Cursor is not null
                            ? r.Cursor
                            : r.SourceIdentityJson is not null
                                ? JsonSerializer.Deserialize<DeploymentDiagnosticSourceIdentity>(r.SourceIdentityJson)
                                    ?.InstanceId ?? "unknown"
                                : "unknown"
                })).GroupBy(p => new { p.Container, p.Sample.Name, p.Sample.Unit, p.Time })
            .Select(g => new DeploymentMetricPoint(g.Key.Container, g.Key.Name, g.Key.Unit, g.Key.Time,
                g.Average(p => p.Sample.Value), g.Min(p => p.Sample.Value), g.Max(p => p.Sample.Value)));
        // Pending samples take precedence over eventual backend copies for their bucket.
        if (options.Value.Specialized &&
            await db.TelemetryTenantStates.AnyAsync(s => s.TenantId == userId && s.DroppedEvents > 0,
                cancellationToken))
            availability = (availability + " Some diagnostics were omitted because storage limits were reached.")
                .Trim();
        return new DeploymentMetricHistory(local.Concat(remote).DistinctBy(p => (p.Container, p.Name, p.Timestamp))
            .OrderBy(p => p.Timestamp).ToArray(), availability);
    }

    /// <inheritdoc />
    public async Task SetRuntimeCollectionAsync(Guid userId, Guid projectId, bool enabled,
        CancellationToken cancellationToken = default)
    {
        var changed = await db.Applications.Where(p => p.Id == projectId && p.UserId == userId)
            .ExecuteUpdateAsync(u => u.SetProperty(p => p.RuntimeDiagnosticsEnabled, enabled), cancellationToken);
        if (changed == 0) throw new UnauthorizedAccessException("Project access denied.");
    }

    /// <summary>Prevents cross-owner and cross-project deployment queries.</summary>
    private async Task AuthorizeAsync(Guid user, Guid project, Guid deployment, CancellationToken token)
    {
        if (!await db.Deployments.AnyAsync(d => d.Id == deployment && d.CsProject!.AppId == project &&
                                                d.CsProject.Application.UserId == user, token))
            throw new UnauthorizedAccessException("Deployment history access denied.");
    }
}