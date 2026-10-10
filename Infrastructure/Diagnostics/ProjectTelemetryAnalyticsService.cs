using Application.Abstractions.Diagnostics;
using Domain.Enums;
using Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Infrastructure.Diagnostics;

/// <summary>Authorizes project access before returning bounded metric projections and SQL deployment summaries.</summary>
public sealed class ProjectTelemetryAnalyticsService(
    AutoMateDbContext db,
    IDiagnosticRedactor redactor,
    IMetricExploration? exploration = null,
    TimeProvider? clock = null,
    IOptions<TelemetryStorageOptions>? storage = null,
    ILogger<ProjectTelemetryAnalyticsService>? logger = null) : IProjectTelemetryAnalytics
{
    /// <inheritdoc />
    public async Task<ProjectTelemetryAnalytics> ReadAsync(Guid user, Guid project, DateTimeOffset start,
        DateTimeOffset end, CancellationToken token = default)
    {
        var range = new MetricTimeRange(start, end);
        range.Validate((clock ?? TimeProvider.System).GetUtcNow());
        if (!await db.Applications.AnyAsync(p => p.Id == project && p.UserId == user, token))
            throw new UnauthorizedAccessException("Project analytics access denied.");
        var selected = db.Deployments.AsNoTracking().Where(d => d.CsProject!.AppId == project &&
                                                                d.CsProject.Application.UserId == user &&
                                                                d.CreatedAt >= start && d.CreatedAt < end);
        var outcomes = await selected.GroupBy(d => d.Outcome)
            .Select(g => new { Outcome = g.Key, Count = g.Count() }).ToListAsync(token);
        var total = outcomes.Sum(g => g.Count);
        var successful = outcomes.Where(g => g.Outcome == DeploymentOutcome.Succeeded).Sum(g => g.Count);
        var failed = outcomes.Where(g => g.Outcome == DeploymentOutcome.Failed).Sum(g => g.Count);
        var duration = await selected.Where(d => d.FinishedAt != null && d.FinishedAt >= d.CreatedAt)
            .Select(d => (double?)(d.FinishedAt!.Value - d.CreatedAt).TotalSeconds).AverageAsync(token);
        if (exploration is not null)
        {
            var observed = await exploration.ReadAsync(new MetricExplorationQuery(user, range, project), token);
            long? errors = null;
            var managed = storage?.Value.ManagedService ?? false;
            var firstDay = new DateTimeOffset(start.UtcDateTime.Date, TimeSpan.Zero);
            if (firstDay < start) firstDay = firstDay.AddDays(1);
            var lastDay = new DateTimeOffset(end.UtcDateTime.Date, TimeSpan.Zero);
            if (firstDay < lastDay)
                try
                {
                    errors = await db.DeploymentDailyTelemetry.AsNoTracking().Where(d => d.UserId == user &&
                            d.ProjectId == project && d.DayUtc >= firstDay && d.DayUtc < lastDay &&
                            d.Metric == "observed_log_errors" &&
                            db.Applications.Any(p => p.Id == project && p.UserId == user &&
                                                     (!managed || p.ManagedTelemetryConsent)))
                        .GroupBy(_ => 1).Select(g => (long?)g.Sum(d => d.ObservedErrors)).FirstOrDefaultAsync(token);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception error)
                {
                    logger?.LogWarning(error, "Metric exploration data unavailable: {FailureType}.",
                        error.GetType().Name);
                }

            return new ProjectTelemetryAnalytics(total, successful, failed, duration,
                observed.Chart.Select(p => new DeploymentAnalyticsRow(p.Deployment, p.Timestamp,
                    redactor.RedactText(p.Container, 128), p.Metric, p.Unit, p.Samples, p.Average,
                    p.Minimum, p.Maximum, 0, p.Incomplete, end)).ToArray(), observed.Availability, observed, errors);
        }

        // Compatibility reads for hosts without the exploration adapter retain an explicit omission notice.
        var daily = await db.DeploymentDailyTelemetry.AsNoTracking().Where(d => d.ProjectId == project &&
                d.UserId == user && d.DayUtc >= start.UtcDateTime.Date && d.DayUtc < end)
            .OrderBy(d => d.DayUtc).Take(10001).ToListAsync(token);
        var notice = daily.Count > 10000
            ? "Result limit reached; narrow the date range."
            : daily.Count == 0
                ? "Daily telemetry is not available yet. Saved deployment history remains available separately."
                : daily.Any(d => d.Incomplete)
                    ? "Some daily statistics contain missing data or approximate log error counts."
                    : null;
        return new ProjectTelemetryAnalytics(total, successful, failed, duration, daily.Take(10000).Select(d =>
            new DeploymentAnalyticsRow(
                d.DeploymentId, d.DayUtc, redactor.RedactText(d.Container, 128), redactor.RedactText(d.Metric, 128),
                redactor.RedactText(d.Unit, 64), d.SampleCount, d.SampleCount > 0 ? d.Sum / d.SampleCount : null,
                d.Minimum, d.Maximum, d.ObservedErrors, d.Incomplete, d.UpdatedAt)).ToArray(), notice);
    }
}