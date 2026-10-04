using System.Text;
using System.Text.Json;
using Application.Abstractions.Diagnostics;
using Domain.Entities;
using Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Infrastructure.Diagnostics;

/// <summary>Bridges durable PostgreSQL ingestion and specialized history without losing recent replay.</summary>
public sealed class DeploymentTelemetryStore(
    AutoMateDbContext db,
    DeploymentDiagnosticStore postgres,
    IDeploymentLogQuery logs,
    IOptions<TelemetryStorageOptions> options,
    IDiagnosticRedactor redactor,
    ILogger<DeploymentTelemetryStore> logger,
    IDeploymentRuntimeViewers viewers,
    ITelemetryGateway? gateway = null,
    TelemetryProjectPolicyCache? policies = null) : IDeploymentDiagnosticStore
{
    /// <inheritdoc />
    public async Task<long> PersistAsync(DeploymentDiagnosticEvent diagnosticEvent, string? terminalChannel,
        CancellationToken cancellationToken = default)
    {
        if (options.Value.DiskGateway)
        {
            var policy = await policies!.GetAsync(diagnosticEvent.ProjectId, cancellationToken);
            if (policy is null) return 0;
            var isRuntime = diagnosticEvent.Kind == DeploymentDiagnosticKind.Metric || diagnosticEvent.Source is
                DeploymentDiagnosticSource.DockerContainer or DeploymentDiagnosticSource.AzureContainerApps;
            if (isRuntime && !policy.RuntimeDiagnosticsEnabled && !(diagnosticEvent.DeploymentId is { } id &&
                                                                    viewers.HasViewers(diagnosticEvent.ProjectId, id)))
                return 0;
            if (options.Value.ManagedService && !policy.ManagedTelemetryConsent) return 0;
            var receipt = await gateway!.AcceptAsync(redactor.Redact(diagnosticEvent).Event with
            {
                EventId = diagnosticEvent.EventId ?? Guid.NewGuid()
            }, terminalChannel, cancellationToken);
            return receipt.OrderId;
        }

        var project = await db.Applications.AsNoTracking().Where(p => p.Id == diagnosticEvent.ProjectId)
            .Select(p => new { p.UserId, p.RuntimeDiagnosticsEnabled, p.ManagedTelemetryConsent })
            .SingleOrDefaultAsync(cancellationToken);
        if (project is null) return 0;
        var runtime = diagnosticEvent.Kind == DeploymentDiagnosticKind.Metric ||
                      diagnosticEvent.Source == DeploymentDiagnosticSource.DockerContainer ||
                      diagnosticEvent.Source == DeploymentDiagnosticSource.AzureContainerApps;
        // Output collected for an authorized viewer must be durable too, so reload/reconnect can replay it.
        // The preference controls unattended collection, rather than discarding already-viewed history.
        if (runtime && !project.RuntimeDiagnosticsEnabled &&
            !(diagnosticEvent.DeploymentId is { } deploymentId &&
              viewers.HasViewers(diagnosticEvent.ProjectId, deploymentId))) return 0;
        var safe = redactor.Redact(diagnosticEvent).Event;
        if (!options.Value.Specialized || (options.Value.ManagedService && !project.ManagedTelemetryConsent))
            return await postgres.PersistAsync(safe, terminalChannel, cancellationToken);
        if (safe.Kind == DeploymentDiagnosticKind.Metric &&
            (safe.DeploymentId is null || safe.Metrics is not { Count: > 0 })) return 0;

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        // The short ingestion lock makes byte admission and identity assignment atomic across instances.
        // No provider request is performed while this lock is held.
        if (db.Database.IsNpgsql())
            await db.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock(73104021)", cancellationToken);
        var now = DateTimeOffset.UtcNow;
        var state = await db.TelemetryTenantStates.SingleOrDefaultAsync(s => s.TenantId == project.UserId,
            cancellationToken);
        if (state is not null) await db.Entry(state).ReloadAsync(cancellationToken);
        if (state is null)
        {
            state = new TelemetryTenantState { TenantId = project.UserId, DueAt = now, LastStoredAt = now };
            db.TelemetryTenantStates.Add(state);
        }

        var json = JsonSerializer.Serialize(safe, TelemetryHttpTransport.Json);
        var bytes = Encoding.UTF8.GetByteCount(json) + Encoding.UTF8.GetByteCount(safe.Message) + 512 +
                    (safe.Metrics is null ? 0 : Encoding.UTF8.GetByteCount(JsonSerializer.Serialize(safe.Metrics))) +
                    (safe.SourceIdentity is null
                        ? 0
                        : Encoding.UTF8.GetByteCount(JsonSerializer.Serialize(safe.SourceIdentity)));
        var global =
            await db.TelemetryTenantStates.SingleOrDefaultAsync(s => s.TenantId == Guid.Empty, cancellationToken);
        if (global is null)
        {
            global = new TelemetryTenantState { TenantId = Guid.Empty, DueAt = now, LastStoredAt = now };
            db.TelemetryTenantStates.Add(global);
        }
        else
        {
            await db.Entry(global).ReloadAsync(cancellationToken);
        }

        if (state.RateWindowStart <= now.AddMinutes(-1))
        {
            state.RateWindowStart = now;
            state.RateWindowBytes = 0;
        }

        var identities = JsonSerializer.Deserialize<Dictionary<string, DateTimeOffset>>(state.MetricIdentitiesJson)!;
        foreach (var expired in identities.Where(p => p.Value <= now).Select(p => p.Key).ToArray())
            identities.Remove(expired);
        var metricIdentity = $"{safe.DeploymentId:N}/{safe.TerminalChannel.Target}";
        if (state.BufferedBytes + bytes > options.Value.TenantBufferBytes ||
            global.BufferedBytes + bytes > options.Value.GlobalBufferBytes ||
            state.RateWindowBytes + bytes > options.Value.TenantBytesPerMinute ||
            (safe.Kind == DeploymentDiagnosticKind.Metric &&
             identities.Count >= options.Value.MaximumMetricContainers &&
             !identities.ContainsKey(metricIdentity)))
        {
            state.DroppedEvents++;
            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            logger.LogWarning("Telemetry admission dropped an event for owner {TenantId}; history is incomplete.",
                project.UserId);
            TelemetryStorageMetrics.Dropped.Add(1);
            return 0;
        }

        // PostgreSQL timestamp precision is one microsecond.
        state.LastStoredAt = now > state.LastStoredAt.AddTicks(10) ? now : state.LastStoredAt.AddTicks(10);
        state.RateWindowBytes += bytes;
        state.BufferedBytes += bytes;
        global.BufferedBytes += bytes;
        if (safe.Kind == DeploymentDiagnosticKind.Metric) identities[metricIdentity] = now.AddDays(30);
        state.MetricIdentitiesJson = JsonSerializer.Serialize(identities);
        var record = new DeploymentDiagnosticRecord
        {
            TenantId = project.UserId,
            ProjectId = safe.ProjectId,
            DeploymentId = safe.DeploymentId,
            TimestampUtc = safe.TimestampUtc,
            Source = safe.Source.ToString(),
            Kind = safe.Kind.ToString(),
            Severity = safe.Severity.ToString(),
            Message = safe.Message,
            TerminalChannel = terminalChannel,
            DeliveryJson = json,
            DeliveryBytes = bytes,
            StoredAt = state.LastStoredAt,
            BufferExpiresAt = now.AddHours(options.Value.BufferHours),
            ExpiresAt = now.AddDays(30),
            Cursor = safe.TerminalChannel.Target,
            MetricSamplesJson = safe.Metrics is null ? null : JsonSerializer.Serialize(safe.Metrics),
            SourceIdentityJson = safe.SourceIdentity is null ? null : JsonSerializer.Serialize(safe.SourceIdentity)
        };
        db.DeploymentDiagnosticRecords.Add(record);
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        TelemetryStorageMetrics.IngestedBytes.Add(bytes);
        return record.OrderId;
    }

    /// <inheritdoc />
    public Task<DeploymentTerminalHistory> ReadRecentAsync(Guid projectId, Guid deploymentId, int limit,
        CancellationToken cancellationToken = default)
    {
        return ReadPageAsync(projectId, deploymentId, 0, true, limit, cancellationToken);
    }

    /// <inheritdoc />
    public Task<DeploymentTerminalHistory> ReadAfterAsync(Guid projectId, Guid deploymentId, long afterOrderId,
        int limit, CancellationToken cancellationToken = default)
    {
        return ReadPageAsync(projectId, deploymentId, afterOrderId, false, limit, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<int> DeleteExpiredAsync(int limit, CancellationToken cancellationToken = default)
    {
        limit = Math.Clamp(limit, 1, 10000);
        var expired = await db.DeploymentDiagnosticRecords.AsNoTracking()
            .Where(r => r.DeliveryJson != null && r.ExpiresAt <= DateTimeOffset.UtcNow)
            .OrderBy(r => r.ExpiresAt).Take(limit).Select(r => new { r.Id, r.TenantId }).ToListAsync(cancellationToken);
        foreach (var group in expired.GroupBy(r => r.TenantId!.Value))
            await TelemetryDeliveryWorker.RemoveBufferedAsync(db, group.Key, group.Select(r => r.Id).ToArray(),
                cancellationToken);
        return expired.Count + (expired.Count == limit
            ? 0
            : await postgres.DeleteExpiredAsync(limit - expired.Count, cancellationToken));
    }

    /// <inheritdoc />
    public async Task<string> BuildContextAsync(Guid deploymentId, int maximumCharacters,
        CancellationToken cancellationToken = default)
    {
        if (!options.Value.Specialized)
            return await postgres.BuildContextAsync(deploymentId, maximumCharacters, cancellationToken);
        var project = await db.Deployments.Where(d => d.Id == deploymentId).Select(d => d.CsProject!.AppId)
            .SingleOrDefaultAsync(cancellationToken);
        var page = await ReadRecentAsync(project, deploymentId, 300, cancellationToken);
        var context = new StringBuilder();
        foreach (var line in page.Events)
        {
            if (context.Length + line.Message.Length > maximumCharacters) break;
            context.AppendLine(line.Message);
        }

        return context.ToString();
    }

    /// <summary>Merges ordered specialized history, legacy rows and unconfirmed durable events.</summary>
    public async Task<DeploymentTerminalHistory> ReadPageAsync(Guid projectId, Guid deploymentId, long cursor,
        bool backwards, int limit, CancellationToken cancellationToken, string? search = null)
    {
        limit = Math.Clamp(limit, 1, 2000);
        var now = DateTimeOffset.UtcNow;
        var project = await db.Applications.AsNoTracking().Where(p => p.Id == projectId)
            .Select(p => new { p.UserId, p.ManagedTelemetryConsent }).SingleOrDefaultAsync(cancellationToken);
        var tenant = project?.UserId ?? Guid.Empty;
        var specialized = options.Value.Specialized &&
                          (!options.Value.ManagedService || project?.ManagedTelemetryConsent == true);
        var deploymentCreatedAt = await db.Deployments
            .Where(d => d.Id == deploymentId && d.CsProject!.AppId == projectId)
            .Select(d => (DateTimeOffset?)d.CreatedAt).SingleOrDefaultAsync(cancellationToken);
        if (tenant == Guid.Empty || deploymentCreatedAt is null) return new DeploymentTerminalHistory([], false);
        if (!specialized && cursor == 0 && backwards && string.IsNullOrEmpty(search))
        {
            var history = await postgres.ReadRecentAsync(projectId, deploymentId, limit, cancellationToken);
            return options.Value.Specialized
                ? history with { Availability = "Managed storage requires owner consent; local history is shown." }
                : history;
        }

        var nextDeploymentCreatedAt = await db.Deployments.Where(d => d.CsProject!.AppId == projectId &&
                                                                      d.CreatedAt > deploymentCreatedAt.Value)
            .OrderBy(d => d.CreatedAt)
            .Select(d => (DateTimeOffset?)d.CreatedAt).FirstOrDefaultAsync(cancellationToken);
        var query = db.DeploymentDiagnosticRecords.AsNoTracking().Where(r =>
            r.ProjectId == projectId && r.ExpiresAt > now &&
            ((r.DeploymentId == deploymentId && r.TerminalChannel != null) ||
             (r.DeploymentId == null && r.DeliveryJson == null &&
              (r.Source == "GitHubActions" || r.Source == "DockerCompose") &&
              r.TimestampUtc >= deploymentCreatedAt.Value &&
              (nextDeploymentCreatedAt == null || r.TimestampUtc < nextDeploymentCreatedAt.Value))));
        if (cursor > 0) query = backwards ? query.Where(r => r.OrderId < cursor) : query.Where(r => r.OrderId > cursor);
        if (!string.IsNullOrEmpty(search)) query = query.Where(r => r.Message.Contains(search));
        var local = await (backwards ? query.OrderByDescending(r => r.OrderId) : query.OrderBy(r => r.OrderId))
            .Take(limit + 1).Select(r => new DeploymentTerminalLog(r.OrderId, r.ProjectId, r.DeploymentId,
                r.TerminalChannel ?? (r.Source == "GitHubActions" ? "github-actions" : "build"), r.Message))
            .ToListAsync(cancellationToken);
        var availability = options.Value.Specialized && !specialized
            ? "Managed storage requires owner consent; local history is shown."
            : null;
        var canAdvance = true;
        if (specialized)
        {
            try
            {
                if (options.Value.DiskGateway)
                {
                    var pending = await gateway!.ReadPendingAsync(tenant, projectId, deploymentId, cancellationToken);
                    local.AddRange(pending.Events.Where(e => e.Channel is not null &&
                                                             (cursor == 0 || (backwards
                                                                 ? e.OrderId < cursor
                                                                 : e.OrderId > cursor)) &&
                                                             (string.IsNullOrEmpty(search) ||
                                                              e.Event.Message.Contains(search,
                                                                  StringComparison.Ordinal)))
                        .Select(e => new DeploymentTerminalLog(e.OrderId, projectId, deploymentId, e.Channel!,
                            e.Event.Message, e.EventId)));
                    if (pending.Events.Count > 0) availability = "Recent history is pending storage confirmation.";
                    if (pending.DroppedEvents > 0 || pending.Truncated)
                        availability = "Some diagnostics were omitted or pending history exceeds the read limit.";
                }

                var remote = !string.IsNullOrEmpty(search) && logs is IDeploymentLogSearch searchable
                    ? await searchable.SearchAsync(tenant, projectId, deploymentId, cursor, backwards, limit + 1,
                        search, cancellationToken)
                    : await logs.ReadAsync(tenant, projectId, deploymentId, cursor, backwards, limit + 1,
                        cancellationToken, deploymentCreatedAt);
                local.AddRange(remote.Select(e => new DeploymentTerminalLog(e.OrderId, e.Event.ProjectId,
                    e.Event.DeploymentId, e.Channel!, e.Event.Message, e.EventId)));
                if (await db.DeploymentDiagnosticRecords.AnyAsync(
                        r => r.ProjectId == projectId && r.DeploymentId == deploymentId && r.DeliveryJson != null,
                        cancellationToken))
                    availability = "Recent history is pending storage confirmation.";
            }
            catch (Exception ex) when (ex is not OperationCanceledException ||
                                       !cancellationToken.IsCancellationRequested)
            {
                logger.LogWarning("Specialized history unavailable for {DeploymentId}: {FailureType}.", deploymentId,
                    ex.GetType().Name);
                availability = "History storage is temporarily unavailable; only buffered output is shown.";
                canAdvance = false;
            }

            if (await db.TelemetryTenantStates.AnyAsync(s => s.TenantId == tenant && s.DroppedEvents > 0,
                    cancellationToken))
                availability = (availability + " Some diagnostics were omitted because storage limits were reached.")
                    .Trim();
        }

        var unique = local.DistinctBy(e => e.EventId is { } id ? id.ToString("N") : "legacy-" + e.OrderId);
        var ordered = backwards ? unique.OrderByDescending(e => e.OrderId) : unique.OrderBy(e => e.OrderId);
        var page = ordered.Take(limit + 1).ToList();
        var more = page.Count > limit;
        if (more) page.RemoveAt(page.Count - 1);
        page.Sort((a, b) => a.OrderId.CompareTo(b.OrderId));
        if (page.Count == 0 && availability is null)
            availability = "No saved output is available; diagnostics expire after 30 days.";
        return new DeploymentTerminalHistory(page, more, availability, canAdvance);
    }

    /// <summary>Restores the same immutable envelope on every delivery attempt.</summary>
    internal static DeploymentLogEnvelope Envelope(DeploymentDiagnosticRecord record)
    {
        return new DeploymentLogEnvelope(record.Id,
            record.TenantId!.Value, record.OrderId, record.StoredAt!.Value, record.ExpiresAt,
            JsonSerializer.Deserialize<DeploymentDiagnosticEvent>(record.DeliveryJson!, TelemetryHttpTransport.Json)!,
            record.TerminalChannel);
    }
}