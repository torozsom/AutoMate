using Application.Abstractions.Ai;
using Application.Abstractions.Diagnostics;
using Application.Ai;
using Infrastructure.Data;
using Infrastructure.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Infrastructure.Ai;

/// <summary>Reads existing Loki/legacy terminal history and Mimir samples into bounded in-memory analysis context.</summary>
public sealed class DeploymentAnalysisContextBuilder(
    AutoMateDbContext db,
    IDeploymentDiagnosticStore diagnostics,
    IDeploymentMetricQuery metrics,
    IDiagnosticRedactor redactor,
    IOptions<AiAnalysisOptions> options,
    IOptions<TelemetryStorageOptions> storage,
    TimeProvider clock) : IDeploymentAnalysisContextBuilder
{
    /// <inheritdoc />
    public async Task<DeploymentAnalysisContext> BuildAsync(Guid deploymentId,
        CancellationToken cancellationToken = default)
    {
        var deployment = await db.Deployments.AsNoTracking().Where(item => item.Id == deploymentId)
            .Select(item => new
            {
                Project = item.CsProject!.AppId,
                Owner = item.CsProject.Application.UserId,
                Consent = item.CsProject.Application.ManagedTelemetryConsent,
                item.CreatedAt
            }).SingleOrDefaultAsync(cancellationToken);
        if (deployment is null) return new DeploymentAnalysisContext("", []);
        var history = await diagnostics.ReadRecentAsync(deployment.Project, deploymentId,
            AnalysisContextSelector.MaximumCandidates, cancellationToken);
        history = history with
        {
            Events = history.Events.Where(row => row.ProjectId == deployment.Project &&
                                                 (row.DeploymentId == deploymentId || row.DeploymentId is null))
                .ToArray()
        };
        IReadOnlyList<DeploymentMetricPoint> points = [];
        var unavailable = storage.Value.ManagedService && !deployment.Consent;
        if (!unavailable)
        {
            var now = clock.GetUtcNow();
            var end = history.Events.Where(row => row.TimestampUtc <= now).Max(row => row.TimestampUtc) ?? now;
            var start = end.AddHours(-1);
            if (start < deployment.CreatedAt) start = deployment.CreatedAt;
            if (start < now.AddDays(-30)) start = now.AddDays(-30);
            try
            {
                if (start < end)
                    points = await metrics.ReadAsync(deployment.Owner, deployment.Project, deploymentId,
                        start, end, 300, cancellationToken);
            }
            catch (Exception) when (!cancellationToken.IsCancellationRequested)
            {
                unavailable = true;
            }
        }

        return new AnalysisContextSelector(redactor).Select(history, points, AnalysisContextBudget.From(options.Value),
            unavailable, cancellationToken);
    }
}