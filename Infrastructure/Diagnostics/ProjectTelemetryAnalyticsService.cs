using Application.Abstractions.Diagnostics;
using Domain.Enums;
using Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Infrastructure.Diagnostics;

/// <summary>Authorizes project access before returning bounded daily statistics and deployment counts.</summary>
public sealed class ProjectTelemetryAnalyticsService(AutoMateDbContext db) : IProjectTelemetryAnalytics
{
    /// <inheritdoc />
    public async Task<ProjectTelemetryAnalytics> ReadAsync(Guid user, Guid project, DateTimeOffset start,
        DateTimeOffset end, CancellationToken token = default)
    {
        if (end <= start || end - start > TimeSpan.FromDays(365))
            throw new ArgumentException("Analytics range must be between 1 and 365 days.");
        if (!await db.Applications.AnyAsync(p => p.Id == project && p.UserId == user, token))
            throw new UnauthorizedAccessException("Project analytics access denied.");
        var now = DateTimeOffset.UtcNow;
        if (start < now.AddDays(-365)) start = now.AddDays(-365);
        var deployments = await db.Deployments.AsNoTracking().Where(d => d.CsProject!.AppId == project &&
                                                                         d.CreatedAt >= start && d.CreatedAt <= end)
            .Select(d => new { d.Id, d.Status, d.CloudGitHubActionRunId }).ToListAsync(token);
        var runs = await db.CloudDeploymentRuns.AsNoTracking().Where(r => r.ProjectId == project && r.UserId == user &&
                                                                          r.CreatedAt >= start && r.CreatedAt <= end &&
                                                                          r.CompletedAt != null)
            .Select(r => new { r.CreatedAt, r.CompletedAt, r.Phase }).ToListAsync(token);
        var durations = runs.Select(r => (r.CompletedAt!.Value - r.CreatedAt).TotalSeconds).Where(s => s >= 0)
            .ToArray();
        var daily = await db.DeploymentDailyTelemetry.AsNoTracking().Where(d => d.ProjectId == project &&
                d.UserId == user &&
                d.DayUtc >= start.UtcDateTime.Date && d.DayUtc <= end)
            .OrderBy(d => d.DayUtc).Take(10001).ToListAsync(token);
        var notice = daily.Count > 10000
            ? "Result limit reached; narrow the date range."
            : daily.Count == 0
                ? "Daily telemetry is not available yet. Detailed history has separate 30-day retention."
                : daily.Any(d => d.Incomplete)
                    ? "Some daily statistics contain missing data or approximate log error counts."
                    : null;
        return new ProjectTelemetryAnalytics(deployments.Count,
            deployments.Count(d => d.Status is DeploymentStatus.Running or DeploymentStatus.Stopped),
            deployments.Count(d => d.Status == DeploymentStatus.Failed),
            durations.Length == 0 ? null : durations.Average(),
            daily.Take(10000).Select(d => new DeploymentAnalyticsRow(d.DeploymentId, d.DayUtc, d.Container,
                d.Metric, d.Unit, d.SampleCount, d.SampleCount > 0 ? d.Sum / d.SampleCount : null,
                d.Minimum, d.Maximum, d.ObservedErrors, d.Incomplete, d.UpdatedAt)).ToArray(), notice);
    }
}