using Application.Abstractions.Diagnostics;
using Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Infrastructure.Diagnostics;

/// <summary>Resolves private batch scopes in SQL; callers cannot supply an unauthorized partition list.</summary>
public sealed class ArchiveMetricBatchReader(
    AutoMateDbContext db,
    IDeploymentArchive archive,
    IOptions<TelemetryStorageOptions> options)
{
    /// <summary>Internal day-boundary fragments may be shorter than the public five-minute window.</summary>
    private static void ValidateArchiveWindow(MetricTimeRange range)
    {
        if (range.Start.Offset != TimeSpan.Zero || range.End.Offset != TimeSpan.Zero || range.End <= range.Start ||
            range.Start < range.End.AddYears(-5) || range.End > DateTimeOffset.UtcNow)
            throw new ArgumentException("Invalid archived metric window.");
    }

    /// <summary>Reads ten authorized partitions per request, with explicit continuation and availability.</summary>
    public async Task<ArchiveMetricBatch> ReadAsync(ArchiveMetricBatchRequest request, CancellationToken token)
    {
        if (options.Value.ManagedService && !options.Value.ManagedDataProcessingApproved)
            throw new InvalidOperationException("Managed telemetry processing is not approved.");
        ValidateArchiveWindow(request.Range);
        if (request.Owner == Guid.Empty || request.Offset < 0) throw new ArgumentException("Invalid metric batch.");
        var projects = db.Applications.AsNoTracking().Where(p => p.UserId == request.Owner);
        if (request.Project is { } project && !await projects.AnyAsync(p => p.Id == project, token))
            throw new UnauthorizedAccessException("Metric access denied.");
        if (request.Deployment is { } deployment && !await db.Deployments.AnyAsync(d => d.Id == deployment &&
                d.CsProject!.Application.UserId == request.Owner &&
                (request.Project == null || d.CsProject.AppId == request.Project), token))
            throw new UnauthorizedAccessException("Metric access denied.");
        var selected = db.Deployments.AsNoTracking().Where(d => d.CsProject!.Application.UserId == request.Owner &&
                                                                d.CreatedAt < request.Range.End &&
                                                                (request.Project == null ||
                                                                 d.CsProject.AppId == request.Project) &&
                                                                (request.Deployment == null ||
                                                                 d.Id == request.Deployment));
        var partitions = await selected.OrderBy(d => d.Id).Skip(request.Offset).Take(11)
            .Select(d => new { d.Id, Project = d.CsProject!.AppId }).ToListAsync(token);
        var items = new List<MetricObservation>();
        string? notice = null;
        foreach (var partition in partitions.Take(10))
        {
            var page = await archive.ReadIndexedMetricsAsync(request.Owner, partition.Project, partition.Id,
                request.Range, request.Container, token);
            var remaining = Math.Max(0, 4000 - items.Count);
            items.AddRange(page.Items.Take(remaining));
            if (page.Items.Count > remaining)
                notice = "Metric batch output limit reached; results are partial. Select a container.";
            else if (page.Availability is not null) notice = page.Availability;
        }

        return new ArchiveMetricBatch(items, partitions.Count > 10 ? request.Offset + 10 : null, notice);
    }
}