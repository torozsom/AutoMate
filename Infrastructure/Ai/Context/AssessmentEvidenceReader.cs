using System.Text.Json;
using Application.Abstractions.Ai.Context;
using Application.Abstractions.Diagnostics.Events;
using Application.Abstractions.Diagnostics.History;
using Application.Abstractions.Diagnostics.Metrics;
using Application.Ai.Context;
using Infrastructure.Data;
using Infrastructure.Diagnostics.Storage;
using Microsoft.EntityFrameworkCore;

namespace Infrastructure.Ai.Context;

/// <summary>
///     Merges bounded selected archive/legacy/backend evidence without retaining unfiltered
///     payloads.
/// </summary>
internal static class AssessmentEvidenceReader
{
    /// <summary>
    ///     Returns detached selected evidence with bounded catalog and explicit omission metadata.
    /// </summary>
    /// <param name="db">Application metadata context used for ownership, leases and persisted state.</param>
    /// <param name="archive">Provides the deployment archive? boundary used by this operation.</param>
    /// <param name="query">Bounded provider query or immutable resource/source selection.</param>
    /// <param name="token">Cancels the underlying operation when observed.</param>
    /// <param name="retainedLogs">Provides the deployment log query? boundary used by this operation.</param>
    /// <returns>A task returning bounded selected evidence and recorded channel metadata.</returns>
    public static async Task<ArchiveAssessmentPage> ReadAsync(AutoMateDbContext db, IDeploymentArchive? archive,
        ArchiveAssessmentQuery query, CancellationToken token, IDeploymentLogQuery? retainedLogs = null)
    {
        var selected = new Dictionary<Guid, DeploymentLogEnvelope>();
        var channels = new HashSet<AssessmentChannel>();
        var containers = new HashSet<string>(StringComparer.Ordinal);
        var omitted = false;
        string? availability = null;
        if (archive is not null)
            try
            {
                var page = archive is IDeploymentAssessmentArchive selectedArchive
                    ? await selectedArchive.ReadAssessmentAsync(query, token)
                    : await archive.ReadAssessmentAsync(query, token);
                foreach (var entry in page.Events)
                    if (entry.TenantId == query.Tenant && entry.Event.ProjectId == query.Project &&
                        entry.Event.DeploymentId == query.Deployment &&
                        AssessmentClassification.Matches(entry, query.Selection, query.Window))
                        selected[entry.EventId] = entry;
                channels.UnionWith(page.Channels);
                containers.UnionWith(page.MetricContainers);
                omitted = page.EarlierOmitted;
            }
            catch (Exception) when (!token.IsCancellationRequested)
            {
                availability = "Archived diagnostics are temporarily unavailable.";
            }

        var now = DateTimeOffset.UtcNow;
        // Enumerate the retained window in order: typed filters apply before the selected-candidate cap.
        var candidates = db.DeploymentDiagnosticRecords.AsNoTracking().Where(r => r.ProjectId == query.Project &&
            r.DeploymentId == query.Deployment);
        var start = query.Window.Start;
        var end = query.Window.End;
        if (db.Database.ProviderName != "Microsoft.EntityFrameworkCore.Sqlite")
        {
            candidates = candidates.Where(r => r.ExpiresAt > now);
            if (!query.CatalogOnly)
                candidates = candidates.Where(r => r.TimestampUtc >= start && r.TimestampUtc <= end);
        }

        var rows = candidates.OrderByDescending(r => r.OrderId).AsAsyncEnumerable();
        var scanned = 0;
        await foreach (var row in rows.WithCancellation(token))
        {
            if (++scanned > 50_000)
            {
                omitted = true;
                break;
            }

            if (row.ExpiresAt <= now ||
                (!query.CatalogOnly && (row.TimestampUtc < start || row.TimestampUtc > end))) continue;
            DeploymentLogEnvelope entry;
            if (row.DeliveryJson is not null && row.TenantId is not null && row.StoredAt is not null)
            {
                entry = DeploymentTelemetryStore.Envelope(row);
            }
            else
            {
                var source = Enum.TryParse<DeploymentDiagnosticSource>(row.Source, out var sourceValue)
                    ? sourceValue
                    : DeploymentDiagnosticSource.DockerContainer;
                var kind = Enum.TryParse<DeploymentDiagnosticKind>(row.Kind, out var kindValue)
                    ? kindValue
                    : DeploymentDiagnosticKind.Log;
                var severity = Enum.TryParse<DeploymentDiagnosticSeverity>(row.Severity, out var level)
                    ? level
                    : DeploymentDiagnosticSeverity.Information;
                var identity = row.SourceIdentityJson is null
                    ? null
                    : JsonSerializer.Deserialize<DeploymentDiagnosticSourceIdentity>(row.SourceIdentityJson);
                var e = new DeploymentDiagnosticEvent(query.Project, query.Deployment, source, kind, severity,
                    row.TimestampUtc,
                    row.Message,
                    new DeploymentTerminalChannel(DeploymentTerminalChannelKind.Container, row.TerminalChannel),
                    TraceId: row.TraceId, SpanId: row.SpanId, Sequence: row.Sequence, SourceIdentity: identity,
                    EventId: row.Id);
                entry = new DeploymentLogEnvelope(row.Id, query.Tenant, row.OrderId, row.TimestampUtc, row.ExpiresAt, e,
                    row.TerminalChannel);
            }

            if (entry.TenantId != query.Tenant || entry.Event.ProjectId != query.Project ||
                entry.Event.DeploymentId != query.Deployment) continue;

            if (entry.Channel is { } channel && entry.Event.Kind != DeploymentDiagnosticKind.Metric &&
                channels.Count < 128)
                channels.Add(new AssessmentChannel(AssessmentClassification.Source(entry.Event), channel));

            if (query.CatalogOnly || !AssessmentClassification.Matches(entry, query.Selection, query.Window)) continue;
            selected.TryAdd(entry.EventId, entry);

            if (selected.Count <= query.Limit) continue;
            var first = selected.MinBy(pair => pair.Value.OrderId);
            selected.Remove(first.Key);
            omitted = true;
        }

        if (query.CatalogOnly || retainedLogs is null ||
            (archive is not null && availability is null && selected.Count != 0))
            return new ArchiveAssessmentPage(
                [
                    .. selected.Values
                        .OrderByDescending(e => e.OrderId)
                        .Take(query.Limit)
                        .OrderBy(e => e.OrderId)
                ],
                [
                    .. channels
                        .OrderBy(c => c.Source)
                        .ThenBy(c => c.Channel, StringComparer.Ordinal)
                        .Take(128)
                ],
                [
                    .. containers
                        .Order(StringComparer.Ordinal)
                        .Take(64)
                ], omitted, availability);
        {
            try
            {
                var backend = await retainedLogs.ReadAssessmentAsync(query, token);
                foreach (var entry in backend)
                    if (entry.TenantId == query.Tenant && entry.Event.ProjectId == query.Project &&
                        entry.Event.DeploymentId == query.Deployment &&
                        AssessmentClassification.Matches(entry, query.Selection, query.Window))
                        selected.TryAdd(entry.EventId, entry);
                if (backend.Count > query.Limit || selected.Count > query.Limit) omitted = true;
            }
            catch (Exception) when (!token.IsCancellationRequested)
            {
                if (archive is null) availability = "Retained diagnostic backend is temporarily unavailable.";
            }
        }

        // Archive persistence precedes ingestion acknowledgment. An available archive is authoritative for current
        // backend-only data; the durable backfill worker imports still-retained older provider records.
        return new ArchiveAssessmentPage(
            [
                .. selected.Values
                    .OrderByDescending(e => e.OrderId)
                    .Take(query.Limit)
                    .OrderBy(e => e.OrderId)
            ],
            [
                .. channels
                    .OrderBy(c => c.Source)
                    .ThenBy(c => c.Channel, StringComparer.Ordinal)
                    .Take(128)
            ],
            [
                .. containers
                    .Order(StringComparer.Ordinal)
                    .Take(64)
            ], omitted, availability);
    }
}
