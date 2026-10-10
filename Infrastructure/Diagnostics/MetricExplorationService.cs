using Application.Abstractions.Diagnostics;
using Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Infrastructure.Diagnostics;

/// <summary>Combines full UTC-day statistics with exact archived boundary observations without double counting.</summary>
public sealed class MetricExplorationService(
    AutoMateDbContext db,
    IDeploymentArchive archive,
    IOptions<TelemetryStorageOptions> options,
    TimeProvider clock,
    IDiagnosticRedactor? redactor = null,
    ILogger<MetricExplorationService>? logger = null) : IMetricExploration
{
    /// <summary>Current masking applies to numeric metadata from legacy database rows too.</summary>
    private readonly IDiagnosticRedactor _redactor = redactor ?? new DiagnosticRedactor();

    /// <inheritdoc />
    public async Task<MetricExplorationResult> ReadAsync(MetricExplorationQuery query,
        CancellationToken token = default)
    {
        query.Range.Validate(clock.GetUtcNow());
        if (query.Owner == Guid.Empty || query.PageSize is not (10 or 25 or 50) || query.Page < 1)
            throw new ArgumentException("Invalid metric exploration request.");
        if (query.Project is { } project &&
            !await db.Applications.AnyAsync(p => p.Id == project && p.UserId == query.Owner, token))
            throw new UnauthorizedAccessException("Metric access denied.");
        if (query.Deployment is { } deployment && !await db.Deployments.AnyAsync(d => d.Id == deployment &&
                d.CsProject!.Application.UserId == query.Owner &&
                (query.Project == null || d.CsProject.AppId == query.Project), token))
            throw new UnauthorizedAccessException("Metric access denied.");
        var rows =
            new Dictionary<(Guid Deployment, string Container, string Metric, string Unit, DateTimeOffset Time),
                MetricObservation>();
        string? notice = null;
        var fullStart = new DateTimeOffset(query.Range.Start.UtcDateTime.Date, TimeSpan.Zero);
        if (fullStart < query.Range.Start) fullStart = fullStart.AddDays(1);
        var fullEnd = new DateTimeOffset(query.Range.End.UtcDateTime.Date, TimeSpan.Zero);
        if (query.Range.End - query.Range.Start > TimeSpan.FromDays(1) && fullStart < fullEnd)
        {
            var daily = db.DeploymentDailyTelemetry.AsNoTracking().Where(d => d.UserId == query.Owner &&
                                                                              d.DayUtc >= fullStart &&
                                                                              d.DayUtc < fullEnd &&
                                                                              (query.Project == null ||
                                                                               d.ProjectId == query.Project) &&
                                                                              (query.Deployment == null ||
                                                                               d.DeploymentId == query.Deployment) &&
                                                                              (query.Container == null ||
                                                                               d.Container == query.Container) &&
                                                                              db.Applications.Any(p =>
                                                                                  p.Id == d.ProjectId &&
                                                                                  p.UserId == query.Owner &&
                                                                                  (!options.Value.ManagedService ||
                                                                                      p.ManagedTelemetryConsent)) &&
                                                                              db.Deployments.Any(deployment =>
                                                                                  deployment.Id == d.DeploymentId &&
                                                                                  deployment.CsProject!.AppId ==
                                                                                  d.ProjectId &&
                                                                                  deployment.CsProject.Application
                                                                                      .UserId == query.Owner) &&
                                                                              d.SampleCount > 0);
            try
            {
                var inspected = 0;
                await foreach (var d in daily.OrderBy(d => d.DayUtc).ThenBy(d => d.DeploymentId)
                                   .ThenBy(d => d.Container).ThenBy(d => d.Metric).AsAsyncEnumerable()
                                   .WithCancellation(token))
                {
                    if (++inspected > 250000)
                    {
                        notice =
                            "Daily metric read budget reached; results are partial. Select a project or container.";
                        break;
                    }

                    Add(new MetricObservation(d.ProjectId, d.DeploymentId, d.Container, d.Metric, d.Unit,
                        query.Range.Bucket(d.DayUtc),
                        d.SampleCount, d.Sum, d.Minimum ?? d.Sum / d.SampleCount,
                        d.Maximum ?? d.Sum / d.SampleCount, d.Incomplete));
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception error)
            {
                logger?.LogWarning(error, "Metric exploration data unavailable: {FailureType}.", error.GetType().Name);
                notice =
                    "Daily resource summaries are unavailable; displayed archived boundary observations are partial.";
            }

            if (query.Range.Start < fullStart) await BoundaryAsync(new MetricTimeRange(query.Range.Start, fullStart));
            if (fullEnd < query.Range.End) await BoundaryAsync(new MetricTimeRange(fullEnd, query.Range.End));
        }
        else
        {
            await BoundaryAsync(query.Range);
        }

        var allowed = new HashSet<Guid>();
        foreach (var projects in rows.Values.Select(p => p.Project).Distinct().Chunk(500))
        foreach (var id in await db.Applications.AsNoTracking().Where(p => projects.Contains(p.Id) &&
                                                                           p.UserId == query.Owner &&
                                                                           (!options.Value.ManagedService ||
                                                                            p.ManagedTelemetryConsent))
                     .Select(p => p.Id).ToListAsync(token))
            allowed.Add(id);
        if (query.Project is { } selectedProject && !await db.Applications.AnyAsync(p =>
                p.Id == selectedProject && p.UserId == query.Owner, token))
            throw new UnauthorizedAccessException("Metric access denied.");
        if (rows.Values.Any(p => !allowed.Contains(p.Project)))
            notice =
                "Ownership or managed-storage consent changed during this read; affected observations are unavailable.";
        var all = rows.Values.Where(p => allowed.Contains(p.Project)).OrderByDescending(p => p.Timestamp)
            .ThenBy(p => p.Deployment)
            .ThenBy(p => p.Container, StringComparer.Ordinal).ThenBy(p => p.Metric, StringComparer.Ordinal).ToArray();
        if (all.Any(p => p.Incomplete)) notice ??= "Some recorded observations are incomplete.";
        if (all.Any(p => p.ImportedIntervals > 0))
            notice =
                (notice + " Imported backend intervals have no raw sample counts and are labeled separately.").Trim();
        if (all.Length == 0) notice ??= "No recorded resource observations in this range.";
        var page = Math.Clamp(query.Page, 1, Math.Max(1, (all.Length + query.PageSize - 1) / query.PageSize));
        return new MetricExplorationResult(all, all.Skip((page - 1) * query.PageSize).Take(query.PageSize).ToArray(),
            all.Length, page, query.PageSize, query.Range, notice);

        /// <summary>Combines only matching metric/deployment/container buckets, retaining real denominators.</summary>
        void Add(MetricObservation value)
        {
            if (!MimirDeploymentMetrics.SupportedUnits.TryGetValue(value.Metric, out var unit) || value.Unit != unit ||
                !double.IsFinite(value.Sum) || !double.IsFinite(value.Minimum) ||
                !double.IsFinite(value.Maximum)) return;
            value = value with
            {
                Timestamp = query.Range.Bucket(value.Timestamp),
                Container = _redactor.RedactText(value.Container, 128)
            };
            var key = (value.Deployment, value.Container, value.Metric, value.Unit, value.Timestamp);
            if (!rows.TryGetValue(key, out var previous))
            {
                if (rows.Count >= 100000)
                {
                    notice =
                        "Metric aggregation limit reached; narrow the range or select a project/container. Results are partial.";
                    return;
                }

                rows[key] = value;
                return;
            }

            if (previous.Samples > 0 && value.ImportedIntervals > 0) return;
            if (value.Samples > 0 && previous.ImportedIntervals > 0)
            {
                rows[key] = value;
                return;
            }

            rows[key] = previous with
            {
                Samples = previous.Samples + value.Samples, Sum = previous.Sum + value.Sum,
                ImportedIntervals = previous.ImportedIntervals + value.ImportedIntervals,
                Minimum = Math.Min(previous.Minimum, value.Minimum),
                Maximum = Math.Max(previous.Maximum, value.Maximum),
                Incomplete = previous.Incomplete || value.Incomplete
            };
        }

        /// <summary>Reads authorized boundary batches and preserves usable data during archive outages.</summary>
        async Task BoundaryAsync(MetricTimeRange range)
        {
            // Boundary fragments may be under five minutes; the public enclosing window has already been validated.
            int? offset = 0;
            var batches = 0;
            while (offset is not null && batches++ < 500)
            {
                token.ThrowIfCancellationRequested();
                ArchiveMetricBatch batch;
                try
                {
                    batch = await archive.ReadMetricBatchAsync(new ArchiveMetricBatchRequest(query.Owner, range,
                        query.Project,
                        query.Deployment, offset.Value, query.Container), token);
                }
                catch (OperationCanceledException) when (!token.IsCancellationRequested)
                {
                    notice = "Detailed metric history timed out; displayed observations are partial.";
                    return;
                }
                catch (Exception error) when (error is HttpRequestException or IOException or InvalidOperationException)
                {
                    logger?.LogWarning(error, "Metric exploration data unavailable: {FailureType}.",
                        error.GetType().Name);
                    notice =
                        "Detailed metric history is unavailable; displayed observations are partial. Update or restore the private Telemetry host, then refresh.";
                    return;
                }

                foreach (var point in batch.Items) Add(point);
                if (batch.Availability is not null) notice = batch.Availability;
                offset = batch.NextOffset;
            }

            if (offset is not null)
                notice =
                    "Some deployment partitions exceed this query's batch budget; results are partial. Select a project.";
        }
    }
}