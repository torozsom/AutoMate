using System.Text;
using System.Text.Json;
using Application.Abstractions.Diagnostics;
using Domain.Entities;
using Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Infrastructure.Diagnostics;

/// <summary>
///     Persists through the disk gateway and merges Loki history with legacy records for
///     replay.
/// </summary>
/// <param name="db">Application metadata context used for ownership, leases and persisted state.</param>
/// <param name="postgres">
///     Retained legacy PostgreSQL history reader; new payload writes are prohibited.
/// </param>
/// <param name="logs">Provides the deployment log query boundary used by this operation.</param>
/// <param name="options">Provides the configured limits and endpoint settings for this service.</param>
/// <param name="redactor">Applies shared credential and unsafe-text masking.</param>
/// <param name="logger">
///     Receives reviewed operational events without diagnostic payloads or credentials.
/// </param>
/// <param name="viewers">Provides the deployment runtime viewers boundary used by this operation.</param>
/// <param name="gateway">Provides the telemetry gateway? boundary used by this operation.</param>
/// <param name="policies">Bounded current project-owner and processing-policy lookup.</param>
/// <param name="archive">Provides the deployment archive? boundary used by this operation.</param>
public sealed class DeploymentTelemetryStore(
    AutoMateDbContext db,
    DeploymentDiagnosticStore postgres,
    IDeploymentLogQuery logs,
    IOptions<TelemetryStorageOptions> options,
    IDiagnosticRedactor redactor,
    ILogger<DeploymentTelemetryStore> logger,
    IDeploymentRuntimeViewers viewers,
    ITelemetryGateway? gateway = null,
    TelemetryProjectPolicyCache? policies = null,
    IDeploymentArchive? archive = null) : IDeploymentDiagnosticStore
{
    /// <summary>
    ///     Retains the legacy persistence contract; current PostgreSQL adapters reject new
    ///     payloads.
    /// </summary>
    /// <param name="diagnosticEvent">Normalized diagnostic to redact before persistence or presentation.</param>
    /// <param name="terminalChannel">The stable terminal routing channel.</param>
    /// <param name="cancellationToken">Cancels the operation when supported by the underlying work.</param>
    /// <returns>The persisted ordering identifier if an adapter supports legacy writes.</returns>
    /// <exception cref="InvalidOperationException">
    ///     New diagnostic payloads require the Loki/Mimir disk gateway.
    /// </exception>
    /// <exception cref="OperationCanceledException">The supplied cancellation token is canceled.</exception>
    public async Task<long> PersistAsync(DeploymentDiagnosticEvent diagnosticEvent, string? terminalChannel,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        // Accepted only for constructor compatibility; viewing no longer controls collection or persistence.
        _ = viewers;
        if (options.Value.DiskGateway)
        {
            var policy = await policies!.GetAsync(diagnosticEvent.ProjectId, cancellationToken);
            if (policy is null) return 0;
            if (diagnosticEvent.DeploymentId is { } deployment && !await db.Deployments.AnyAsync(d =>
                    d.Id == deployment && d.CsProject!.AppId == diagnosticEvent.ProjectId &&
                    d.CsProject.Application.UserId == policy.UserId, cancellationToken)) return 0;
            var receipt = await gateway!.AcceptAsync(redactor.Redact(diagnosticEvent).Event with
            {
                EventId = diagnosticEvent.EventId ?? Guid.NewGuid()
            }, terminalChannel is null ? null : redactor.RedactText(terminalChannel, 128), cancellationToken);
            return receipt.OrderId;
        }

        throw new InvalidOperationException("New diagnostic payloads require the Loki/Mimir disk gateway.");
    }

    /// <summary>
    ///     Reads a bounded recent terminal-history page for one deployment.
    /// </summary>
    /// <param name="projectId">The project identity used for authorization or correlation.</param>
    /// <param name="deploymentId">The exact deployment identity used for ownership or correlation.</param>
    /// <param name="limit">The maximum number of returned or processed records.</param>
    /// <param name="cancellationToken">Cancels the operation when supported by the underlying work.</param>
    /// <returns>
    ///     A task returning recent terminal records with explicit omissions and availability
    ///     metadata.
    /// </returns>
    public Task<DeploymentTerminalHistory> ReadRecentAsync(Guid projectId, Guid deploymentId, int limit,
        CancellationToken cancellationToken = default)
    {
        return ReadPageAsync(projectId, deploymentId, 0, true, limit, cancellationToken);
    }

    /// <summary>
    ///     Reads a bounded ordered page after a previously delivered terminal event.
    /// </summary>
    /// <param name="projectId">The project identity used for authorization or correlation.</param>
    /// <param name="deploymentId">The exact deployment identity used for ownership or correlation.</param>
    /// <param name="afterOrderId">The last confirmed ordering position; return only later records.</param>
    /// <param name="limit">The maximum number of returned or processed records.</param>
    /// <param name="cancellationToken">Cancels the operation when supported by the underlying work.</param>
    /// <returns>
    ///     A task returning ordered terminal records following the confirmed cursor, with coverage
    ///     metadata.
    /// </returns>
    public Task<DeploymentTerminalHistory> ReadAfterAsync(Guid projectId, Guid deploymentId, long afterOrderId,
        int limit, CancellationToken cancellationToken = default)
    {
        return ReadPageAsync(projectId, deploymentId, afterOrderId, false, limit, cancellationToken);
    }

    /// <summary>
    ///     Deletes a bounded batch of expired diagnostic records.
    /// </summary>
    /// <param name="limit">The maximum number of returned or processed records.</param>
    /// <param name="cancellationToken">Cancels the operation when supported by the underlying work.</param>
    /// <returns>A task returning the number of expired records deleted in this batch.</returns>
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

    /// <summary>
    ///     Builds bounded redacted compatibility context for one deployment.
    /// </summary>
    /// <param name="deploymentId">The exact deployment identity used for ownership or correlation.</param>
    /// <param name="maximumCharacters">The maximum permitted text length.</param>
    /// <param name="cancellationToken">Cancels the operation when supported by the underlying work.</param>
    /// <returns>A task returning bounded redacted diagnostic text suitable for legacy context callers.</returns>
    public async Task<string> BuildContextAsync(Guid deploymentId, int maximumCharacters,
        CancellationToken cancellationToken = default)
    {
        if (!options.Value.Specialized)
            return redactor.RedactText(
                await postgres.BuildContextAsync(deploymentId, maximumCharacters, cancellationToken),
                maximumCharacters);
        var project = await db.Deployments.Where(d => d.Id == deploymentId).Select(d => d.CsProject!.AppId)
            .SingleOrDefaultAsync(cancellationToken);
        var page = await ReadRecentAsync(project, deploymentId, 300, cancellationToken);
        var context = new StringBuilder();
        foreach (var line in page.Events)
        {
            if (context.Length + line.Message.Length > maximumCharacters) break;
            context.AppendLine(line.Message);
        }

        return redactor.RedactText(context.ToString(), maximumCharacters);
    }

    /// <summary>
    ///     Merges ordered specialized history, legacy rows and unconfirmed durable events.
    /// </summary>
    /// <param name="projectId">The project identity used for authorization or correlation.</param>
    /// <param name="deploymentId">
    ///     The deployment identity; null is used only where legacy observations permit it.
    /// </param>
    /// <param name="cursor">The previous read position; an absent cursor selects the initial page.</param>
    /// <param name="backwards">Whether to read earlier records rather than records after the cursor.</param>
    /// <param name="limit">The maximum number of returned or processed records.</param>
    /// <param name="cancellationToken">Cancels the underlying operation when observed.</param>
    /// <param name="search">The optional plain-text search term.</param>
    /// <returns>A task returning bounded ordered history with omission and availability metadata.</returns>
    public async Task<DeploymentTerminalHistory> ReadPageAsync(Guid projectId, Guid deploymentId, long cursor,
        bool backwards, int limit, CancellationToken cancellationToken, string? search = null)
    {
        limit = Math.Clamp(limit, 1, 2000);
        var now = DateTimeOffset.UtcNow;
        var project = await db.Applications.AsNoTracking().Where(p => p.Id == projectId)
            .Select(p => new { p.UserId }).SingleOrDefaultAsync(cancellationToken);
        var tenant = project?.UserId ?? Guid.Empty;
        var specialized = options.Value.Specialized;
        var deploymentCreatedAt = await db.Deployments
            .Where(d => d.Id == deploymentId && d.CsProject!.AppId == projectId)
            .Select(d => (DateTimeOffset?)d.CreatedAt).SingleOrDefaultAsync(cancellationToken);
        if (tenant == Guid.Empty || deploymentCreatedAt is null) return new DeploymentTerminalHistory([], false);
        if (!specialized && cursor == 0 && backwards && string.IsNullOrEmpty(search))
        {
            var history = await postgres.ReadRecentAsync(projectId, deploymentId, limit, cancellationToken);
            history = history with { Events = history.Events.Select(redactor.RedactTerminal).ToArray() };
            return history;
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
                r.TerminalChannel ?? (r.Source == "GitHubActions" ? "github-actions" : "build"), r.Message, r.Id, null,
                r.Severity == "Critical" ? DeploymentDiagnosticSeverity.Critical :
                r.Severity == "Error" ? DeploymentDiagnosticSeverity.Error :
                r.Severity == "Warning" ? DeploymentDiagnosticSeverity.Warning :
                r.Severity == "Information" ? DeploymentDiagnosticSeverity.Information :
                r.Severity == "Debug" ? DeploymentDiagnosticSeverity.Debug :
                r.Severity == "Trace" ? DeploymentDiagnosticSeverity.Trace : null,
                r.TimestampUtc, null, null, r.TraceId, r.SpanId, r.Sequence))
            .ToListAsync(cancellationToken);
        string? availability = null;
        var canAdvance = true;
        var archiveAvailable = false;
        if (specialized)
        {
            try
            {
                if (archive is not null)
                {
                    var archived = await archive.ReadAsync(tenant, projectId, deploymentId, cursor, backwards,
                        limit + 1, search, cancellationToken);
                    local.AddRange(
                        archived.Select(e => DeploymentTerminalLog.FromEvent(e.OrderId, e.Event, e.Channel!)));
                    archiveAvailable = true;
                }

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
                        .Select(e => DeploymentTerminalLog.FromEvent(e.OrderId, e.Event, e.Channel!)));
                    if (pending.Events.Count > 0) availability = "Recent history is pending storage confirmation.";
                    if (pending.DroppedEvents > 0 || pending.Truncated)
                        availability = "Some diagnostics were omitted or pending history exceeds the read limit.";
                }

                var remote = !string.IsNullOrEmpty(search) && logs is IDeploymentLogSearch searchable
                    ? await searchable.SearchAsync(tenant, projectId, deploymentId, cursor, backwards, limit + 1,
                        search, cancellationToken)
                    : await logs.ReadAsync(tenant, projectId, deploymentId, cursor, backwards, limit + 1,
                        cancellationToken, deploymentCreatedAt);
                local.AddRange(remote.Select(e => DeploymentTerminalLog.FromEvent(e.OrderId, e.Event, e.Channel!)));
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
                availability = ex is TelemetryCompatibilityException compatibilityError
                    ? compatibilityError.Message
                    : archiveAvailable
                        ? "Saved archive output is available; operational storage is temporarily unavailable."
                        : "History storage is temporarily unavailable; only buffered output is shown.";
                canAdvance = archiveAvailable;
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
            availability =
                "No saved output is available for this deployment. Older data may predate permanent history.";
        return new DeploymentTerminalHistory(page.Select(redactor.RedactTerminal).ToArray(), more, availability,
            canAdvance);
    }

    /// <summary>
    ///     Restores the same immutable envelope on every delivery attempt.
    /// </summary>
    /// <param name="record">Provider observation or retained diagnostic record to process.</param>
    /// <returns>The immutable redacted delivery envelope with its stable identity.</returns>
    internal static DeploymentLogEnvelope Envelope(DeploymentDiagnosticRecord record)
    {
        return new DeploymentLogEnvelope(record.Id,
            record.TenantId!.Value, record.OrderId, record.StoredAt!.Value, record.ExpiresAt,
            JsonSerializer.Deserialize<DeploymentDiagnosticEvent>(record.DeliveryJson!, TelemetryHttpTransport.Json)!,
            record.TerminalChannel);
    }
}