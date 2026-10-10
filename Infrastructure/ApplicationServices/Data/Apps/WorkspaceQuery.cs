using Application.Abstractions.Diagnostics;
using Application.Data.Apps;
using Domain.Enums;
using Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Infrastructure.ApplicationServices.Data.Apps;

/// <summary>Projects bounded owner-scoped metadata and aggregate queries without provider calls.</summary>
public sealed class WorkspaceQuery(
    AutoMateDbContext db,
    TimeProvider clock,
    ILogger<WorkspaceQuery>? logger = null,
    IMetricExploration? exploration = null)
    : IWorkspaceQuery
{
    /// <inheritdoc />
    public async Task<ProjectInventoryPage> ProjectsAsync(Guid owner, ProjectInventoryRequest request,
        CancellationToken token = default)
    {
        RequireOwner(owner);
        var apps = db.Applications.AsNoTracking().Where(a => a.UserId == owner);
        var saved = await apps.CountAsync(token);
        var github = await apps.CountAsync(a => a.SourceType == SourceType.Remote, token);
        var rows = apps.Select(a => new
        {
            a.Id, a.Name, Source = a.SourcePathOrUrl, a.SourceType,
            Components = a.CsProjects.Count, WebApps = a.CsProjects.Count(c => c.IsWebProject),
            SavedAt = a.CreatedAt,
            LatestAt = a.CsProjects.SelectMany(c => c.Deployments).OrderByDescending(d => d.CreatedAt)
                .ThenByDescending(d => d.Id)
                .Select(d => (DateTimeOffset?)d.CreatedAt).FirstOrDefault(),
            Status = a.CsProjects.SelectMany(c => c.Deployments).OrderByDescending(d => d.CreatedAt)
                .ThenByDescending(d => d.Id)
                .Select(d => (DeploymentStatus?)d.Status).FirstOrDefault()
        });
        var running = await rows.CountAsync(a => a.Status == DeploymentStatus.Running, token);
        var search = request.Search?.Trim();
        if (!string.IsNullOrEmpty(search))
        {
            search = search[..Math.Min(search.Length, 200)].ToLower();
            rows = rows.Where(a => a.Name.ToLower().Contains(search) || a.Source.ToLower().Contains(search));
        }

        if (request.Source is { } source) rows = rows.Where(a => a.SourceType == source);
        if (request.Status is { } status) rows = rows.Where(a => a.Status == status);
        var total = await rows.CountAsync(token);
        var page = Math.Clamp(request.Page, 1, Math.Max(1, (total + 19) / 20));
        rows = request.Sort switch
        {
            "name" => rows.OrderBy(a => a.Name).ThenBy(a => a.Id),
            "saved" => rows.OrderByDescending(a => a.SavedAt).ThenBy(a => a.Id),
            _ => rows.OrderByDescending(a => a.LatestAt ?? a.SavedAt).ThenBy(a => a.Id)
        };
        return new ProjectInventoryPage(await rows.Skip((page - 1) * 20).Take(20)
                .Select(a => new ProjectInventoryRow(a.Id, a.Name, a.Source, a.SourceType, a.Components, a.WebApps,
                    a.SavedAt, a.LatestAt, a.Status)).ToListAsync(token),
            total, page, saved, running, github, saved - github);
    }

    /// <inheritdoc />
    public async Task<WorkspaceOverview> OverviewAsync(Guid owner, int days, CancellationToken token = default)
    {
        RequireOwner(owner);
        if (days is not (7 or 30 or 90)) throw new ArgumentOutOfRangeException(nameof(days));
        var end = clock.GetUtcNow();
        var start = new DateTimeOffset(end.UtcDateTime.Date.AddDays(1 - days), TimeSpan.Zero);
        return await OverviewAsync(owner, new MetricTimeRange(start, end), token);
    }

    /// <inheritdoc />
    public async Task<WorkspaceOverview> OverviewAsync(Guid owner, MetricTimeRange range,
        CancellationToken token = default)
    {
        RequireOwner(owner);
        range.Validate(clock.GetUtcNow());
        var start = range.Start;
        var end = range.End;
        var all = db.Deployments.AsNoTracking().Where(d => d.CsProject!.Application.UserId == owner);
        var selected = all.Where(d => d.CreatedAt >= start && d.CreatedAt < end);
        var counts = await selected.GroupBy(d => d.Outcome).Select(g => new { Outcome = g.Key, Count = g.Count() })
            .ToListAsync(token);
        var shortWindow = end - start <= TimeSpan.FromDays(1);
        var activity = await selected
            .GroupBy(d => new
            {
                d.CreatedAt.Year, d.CreatedAt.Month, d.CreatedAt.Day,
                Hour = shortWindow ? d.CreatedAt.Hour : 0, Minute = shortWindow ? d.CreatedAt.Minute : 0, d.Outcome
            })
            .Select(g => new
                { g.Key.Year, g.Key.Month, g.Key.Day, g.Key.Hour, g.Key.Minute, g.Key.Outcome, Count = g.Count() })
            .ToListAsync(token);
        var duration = await selected.Where(d => d.FinishedAt != null && d.FinishedAt >= d.CreatedAt)
            .Select(d => (double?)(d.FinishedAt!.Value - d.CreatedAt).TotalSeconds).AverageAsync(token);
        var recent = await all.OrderByDescending(d => d.CreatedAt).ThenByDescending(d => d.Id).Take(10)
            .Select(d => new WorkspaceRun(d.CsProject!.AppId, d.Id, d.CsProject.Application.Name,
                d.CsProject.Application.SourceType == SourceType.Remote ? "Azure" : "Docker", d.CreatedAt, d.Outcome,
                d.Status,
                d.FinishedAt != null && d.FinishedAt >= d.CreatedAt
                    ? (d.FinishedAt.Value - d.CreatedAt).TotalSeconds
                    : null))
            .ToListAsync(token);
        // Only the latest record in each component is current; older failed attempts belong to history.
        var attention = await all.Where(d =>
                (d.Status == DeploymentStatus.Starting || d.Status == DeploymentStatus.Failed) &&
                !db.Deployments.Any(newer => newer.CsProjectId == d.CsProjectId && newer.CreatedAt > d.CreatedAt))
            .OrderByDescending(d => d.CreatedAt).Take(10)
            .Select(d =>
                new WorkspaceAttention(d.CsProject!.AppId, d.CsProject.Application.Name, d.Status.ToString(), d.Id))
            .ToListAsync(token);
        var queued = await db.CloudDeploymentRuns.AsNoTracking().Where(r => r.UserId == owner &&
                                                                            r.Phase == CloudRunPhase.Queued &&
                                                                            r.DeploymentId == null &&
                                                                            db.Applications.Any(a =>
                                                                                a.Id == r.ProjectId &&
                                                                                a.UserId == owner))
            .OrderBy(r => r.CreatedAt).Take(10).Select(r => new WorkspaceAttention(r.ProjectId,
                db.Applications.Where(a => a.Id == r.ProjectId).Select(a => a.Name).First(), "Queued", null))
            .ToListAsync(token);
        IReadOnlyList<WorkspaceResource> resources = [];
        string? notice = null;
        MetricExplorationResult? observations = null;
        try
        {
            if (exploration is not null)
            {
                var observed = await exploration.ReadAsync(new MetricExplorationQuery(owner, range), token);
                observations = observed;
                resources = observed.Chart.GroupBy(p => new { p.Timestamp, p.Metric, p.Unit }).Select(g =>
                {
                    var raw = g.Where(p => p.Samples > 0).ToArray();
                    var values = raw.Length > 0 ? raw : g.ToArray();
                    var denominator = values.Sum(p => p.Samples > 0 ? p.Samples : p.ImportedIntervals);
                    return new WorkspaceResource(g.Key.Timestamp, g.Key.Metric, g.Key.Unit,
                        raw.Sum(p => p.Samples), values.Sum(p => p.Sum) / Math.Max(1, denominator),
                        values.Min(p => p.Minimum), values.Max(p => p.Maximum), values.Any(p => p.Incomplete));
                }).OrderBy(p => p.Day).ToArray();
                notice = observed.Availability;
            }
            else
            {
                resources = await db.DeploymentDailyTelemetry.AsNoTracking().Where(t => t.UserId == owner &&
                        db.Applications.Any(a => a.Id == t.ProjectId && a.UserId == owner) &&
                        t.DayUtc >= start && t.DayUtc <= end && t.SampleCount > 0 &&
                        (t.Metric == "automate_cpu_usage_cores" || t.Metric == "automate_memory_used_bytes"))
                    .GroupBy(t => new { t.DayUtc, t.Metric, t.Unit })
                    .Select(g => new
                    {
                        Day = g.Key.DayUtc, g.Key.Metric, g.Key.Unit, Samples = g.Sum(t => t.SampleCount),
                        Average = g.Sum(t => t.Sum) / g.Sum(t => t.SampleCount), Minimum = g.Min(t => t.Minimum),
                        Maximum = g.Max(t => t.Maximum),
                        Incomplete = g.Any(t => t.Incomplete)
                    }).OrderBy(t => t.Day)
                    .Select(t => new WorkspaceResource(t.Day, t.Metric, t.Unit, t.Samples, t.Average, t.Minimum,
                        t.Maximum,
                        t.Incomplete)).ToListAsync(token);
            }

            notice ??= resources.Count == 0 ? "No recorded resource observations in this period."
                : resources.Any(r => r.Incomplete) ? "Some recorded daily statistics are incomplete." : null;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception error)
        {
            logger?.LogWarning("Workspace resource query failed: {FailureType}.", error.GetType().Name);
            notice = "Resource statistics are temporarily unavailable. Deployment activity is still available.";
        }

        var activityCounts = activity.GroupBy(a => range.Bucket(new DateTimeOffset(a.Year, a.Month, a.Day,
            a.Hour, a.Minute, 0, TimeSpan.Zero))).ToDictionary(g => g.Key, g => new WorkspaceActivity(g.Key,
            g.Where(a => a.Outcome == DeploymentOutcome.Succeeded).Sum(a => a.Count),
            g.Where(a => a.Outcome == DeploymentOutcome.Failed).Sum(a => a.Count),
            g.Where(a => a.Outcome == DeploymentOutcome.Unknown).Sum(a => a.Count)));
        var buckets = new List<WorkspaceActivity>();
        for (var time = range.Bucket(start);
             time < end && buckets.Count < 240;
             time = range.Aggregation == "UTC calendar months" ? time.AddMonths(1) : time.Add(range.Interval))
            buckets.Add(activityCounts.GetValueOrDefault(time) ?? new WorkspaceActivity(time, 0, 0, 0));
        return new WorkspaceOverview(start, end, await db.Applications.CountAsync(a => a.UserId == owner, token),
            await all.CountAsync(d => d.Status == DeploymentStatus.Running, token), counts.Sum(c => c.Count),
            counts.Where(c => c.Outcome == DeploymentOutcome.Succeeded).Sum(c => c.Count),
            counts.Where(c => c.Outcome == DeploymentOutcome.Failed).Sum(c => c.Count),
            counts.Where(c => c.Outcome == DeploymentOutcome.Unknown).Sum(c => c.Count), duration, buckets, resources,
            recent, attention.Concat(queued).ToArray(), notice, observations);
    }

    /// <summary>Rejects missing internal identities rather than treating anonymous users as owners.</summary>
    private static void RequireOwner(Guid owner)
    {
        if (owner == Guid.Empty) throw new UnauthorizedAccessException("An authenticated workspace owner is required.");
    }
}