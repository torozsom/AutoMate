using System.Text.Json;
using Application.Abstractions.Diagnostics;
using Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Infrastructure.Diagnostics;

/// <summary>
///     Authorizes deployment history before any provider query or preference change.
/// </summary>
/// <param name="db">Application metadata context used for ownership, leases and persisted state.</param>
/// <param name="store">
///     Deployment telemetry store combining durable gateway writes with retained legacy reads.
/// </param>
/// <param name="metrics">Recorded numeric samples or aggregate points.</param>
/// <param name="options">Provides the configured limits and endpoint settings for this service.</param>
/// <param name="gateway">Provides the telemetry gateway? boundary used by this operation.</param>
/// <param name="archive">Provides the deployment archive? boundary used by this operation.</param>
public sealed class DeploymentHistoryService(
    AutoMateDbContext db,
    DeploymentTelemetryStore store,
    IDeploymentMetricQuery metrics,
    IOptions<TelemetryStorageOptions> options,
    ITelemetryGateway? gateway = null,
    IDeploymentArchive? archive = null) : IDeploymentHistoryService
{
    /// <summary>
    ///     Reads authorized terminal history using versioned resource-scoped navigation cursors.
    /// </summary>
    /// <param name="user">The internal user identity used for authorization.</param>
    /// <param name="project">The project identity used to scope the operation.</param>
    /// <param name="deployment">The exact deployment identity used to scope the operation.</param>
    /// <param name="cursor">The previous read position; an absent cursor selects the initial page.</param>
    /// <param name="backwards">Whether to read earlier records rather than records after the cursor.</param>
    /// <param name="limit">The maximum number of returned or processed records.</param>
    /// <param name="search">The optional plain-text search term.</param>
    /// <param name="token">Cancels the operation when supported by the underlying work.</param>
    /// <returns>
    ///     A task returning an owned terminal page with opaque navigation cursors and availability
    ///     metadata.
    /// </returns>
    /// <exception cref="ArgumentException">Log search is limited to 256 characters.</exception>
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

    /// <summary>
    ///     Reads owner-scoped automatic collection status and processing location.
    /// </summary>
    /// <param name="userId">The internal AutoMate user identity.</param>
    /// <param name="projectId">The project identity used for authorization or correlation.</param>
    /// <param name="cancellationToken">Cancels the operation when supported by the underlying work.</param>
    /// <returns>
    ///     A task returning the owned saved selection and source catalog, or null for missing or
    ///     unauthorized deployments.
    /// </returns>
    /// <exception cref="UnauthorizedAccessException">Project access denied.</exception>
    public async Task<DeploymentTelemetryPreferences> GetPreferencesAsync(Guid userId, Guid projectId,
        CancellationToken cancellationToken = default)
    {
        var project = await db.Applications.AsNoTracking()
            .SingleOrDefaultAsync(p => p.Id == projectId && p.UserId == userId, cancellationToken);
        if (project is null) throw new UnauthorizedAccessException("Project access denied.");
        return new DeploymentTelemetryPreferences(true, true,
            options.Value.ManagedService, options.Value.ProcessingRegion);
    }

    /// <summary>
    ///     Compatibility operation; authorizes the owner and keeps approved telemetry storage
    ///     enabled.
    /// </summary>
    /// <param name="userId">The internal AutoMate user identity.</param>
    /// <param name="projectId">The project identity used for authorization or correlation.</param>
    /// <param name="enabled">The legacy preference value; approved automatic collection remains enabled.</param>
    /// <param name="cancellationToken">Cancels the operation when supported by the underlying work.</param>
    /// <returns>A task that completes after owner authorization and the compatibility storage update.</returns>
    /// <exception cref="InvalidOperationException">Managed provider onboarding is not configured.</exception>
    /// <exception cref="UnauthorizedAccessException">Project access denied.</exception>
    public async Task SetManagedConsentAsync(Guid userId, Guid projectId, bool enabled,
        CancellationToken cancellationToken = default)
    {
        if (options.Value.ManagedService && !options.Value.ManagedDataProcessingApproved)
            throw new InvalidOperationException("Managed provider onboarding is not configured.");
        var changed = await db.Applications.Where(p => p.Id == projectId && p.UserId == userId)
            .ExecuteUpdateAsync(u => u.SetProperty(p => p.ManagedTelemetryConsent, true), cancellationToken);
        if (changed == 0) throw new UnauthorizedAccessException("Project access denied.");
    }

    /// <summary>
    ///     Reads terminal output only after checking project and deployment ownership.
    /// </summary>
    /// <param name="userId">The internal AutoMate user identity.</param>
    /// <param name="projectId">The project identity used for authorization or correlation.</param>
    /// <param name="deploymentId">The exact deployment identity used for ownership or correlation.</param>
    /// <param name="cursor">The previous read position; an absent cursor selects the initial page.</param>
    /// <param name="backwards">Whether to read earlier records rather than records after the cursor.</param>
    /// <param name="limit">The maximum number of returned or processed records.</param>
    /// <param name="cancellationToken">Cancels the operation when supported by the underlying work.</param>
    /// <returns>
    ///     A task returning owned terminal output with explicit omissions and storage availability.
    /// </returns>
    public async Task<DeploymentTerminalHistory> ReadLogsAsync(Guid userId, Guid projectId, Guid deploymentId,
        long cursor, bool backwards, int limit, CancellationToken cancellationToken = default)
    {
        await AuthorizeAsync(userId, projectId, deploymentId, cancellationToken);
        return await store.ReadPageAsync(projectId, deploymentId, Math.Max(0, cursor), backwards, limit,
            cancellationToken);
    }

    /// <summary>
    ///     Reads bounded numeric history only after checking ownership.
    /// </summary>
    /// <param name="userId">The internal AutoMate user identity.</param>
    /// <param name="projectId">The project identity used for authorization or correlation.</param>
    /// <param name="deploymentId">The exact deployment identity used for ownership or correlation.</param>
    /// <param name="start">The lower bound of the selected observation window.</param>
    /// <param name="end">The upper bound of the selected observation window.</param>
    /// <param name="maximumPoints">The maximum number of returned aggregate points.</param>
    /// <param name="cancellationToken">Cancels the operation when supported by the underlying work.</param>
    /// <returns>A task returning bounded numeric aggregates and any storage-availability notice.</returns>
    /// <exception cref="ArgumentException">Metric range must be positive.</exception>
    public async Task<DeploymentMetricHistory> ReadMetricsAsync(Guid userId, Guid projectId, Guid deploymentId,
        DateTimeOffset start, DateTimeOffset end, int maximumPoints, CancellationToken cancellationToken = default)
    {
        await AuthorizeAsync(userId, projectId, deploymentId, cancellationToken);
        var now = DateTimeOffset.UtcNow;
        if (end <= start)
            throw new ArgumentException("Metric range must be positive.");
        if (archive is null) start = start < now.AddDays(-30) ? now.AddDays(-30) : start;
        end = end > now ? now : end;
        if (end <= start) return new DeploymentMetricHistory([], "Metric history has expired.");
        maximumPoints = Math.Clamp(maximumPoints, 1, 1000);
        if (archive is not null && options.Value.DiskGateway)
        {
            var saved = await archive.ReadMetricsAsync(userId, projectId, deploymentId, start, end, maximumPoints,
                cancellationToken);
            if (saved.Count > 0) return new DeploymentMetricHistory(saved);
        }

        IReadOnlyList<DeploymentMetricPoint> remote = [];
        string? availability = null;
        var specialized = options.Value.Specialized;
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

    /// <summary>
    ///     Compatibility operation; authorizes the owner and keeps runtime collection enabled.
    /// </summary>
    /// <param name="userId">The internal AutoMate user identity.</param>
    /// <param name="projectId">The project identity used for authorization or correlation.</param>
    /// <param name="enabled">The legacy preference value; approved automatic collection remains enabled.</param>
    /// <param name="cancellationToken">Cancels the operation when supported by the underlying work.</param>
    /// <returns>
    ///     A task that completes after owner authorization and the compatibility collection update.
    /// </returns>
    /// <exception cref="UnauthorizedAccessException">Project access denied.</exception>
    public async Task SetRuntimeCollectionAsync(Guid userId, Guid projectId, bool enabled,
        CancellationToken cancellationToken = default)
    {
        var changed = await db.Applications.Where(p => p.Id == projectId && p.UserId == userId)
            .ExecuteUpdateAsync(u => u.SetProperty(p => p.RuntimeDiagnosticsEnabled, true), cancellationToken);
        if (changed == 0) throw new UnauthorizedAccessException("Project access denied.");
    }

    /// <summary>
    ///     Prevents cross-owner and cross-project deployment queries.
    /// </summary>
    /// <param name="user">The local or connected account whose metadata is being updated.</param>
    /// <param name="project">The owned project identity or normalized local import metadata.</param>
    /// <param name="deployment">The exact deployment identity or tracked metadata for this operation.</param>
    /// <param name="token">Cancels the underlying operation when observed.</param>
    /// <returns>A task representing completion of the asynchronous operation.</returns>
    /// <exception cref="UnauthorizedAccessException">Deployment history access denied.</exception>
    private async Task AuthorizeAsync(Guid user, Guid project, Guid deployment, CancellationToken token)
    {
        if (!await db.Deployments.AnyAsync(d => d.Id == deployment && d.CsProject!.AppId == project &&
                                                d.CsProject.Application.UserId == user, token))
            throw new UnauthorizedAccessException("Deployment history access denied.");
    }
}