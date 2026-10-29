using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Application.Abstractions.Ai.Context;
using Application.Abstractions.Diagnostics.Events;
using Application.Abstractions.Diagnostics.History;
using Application.Abstractions.Diagnostics.Metrics;
using Application.Abstractions.Diagnostics.Redaction;
using Application.Ai.Context;
using Application.Ai.Policy;
using Domain.Entities.Deployments;
using Infrastructure.Data;
using Infrastructure.Diagnostics.Policy;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Infrastructure.Ai.Context;

/// <summary>
///     Collects selected deployment evidence and freezes status/configuration provenance at
///     collection time.
/// </summary>
/// <param name="db">Application metadata context used for ownership, leases and persisted state.</param>
/// <param name="diagnostics">
///     Optional bounded redacted console exception sink, separate from exported telemetry.
/// </param>
/// <param name="metrics">Recorded numeric samples or aggregate points.</param>
/// <param name="redactor">Applies shared credential and unsafe-text masking.</param>
/// <param name="options">Provides the configured limits and endpoint settings for this service.</param>
/// <param name="storage">Provides the configured limits and endpoint settings for this service.</param>
/// <param name="clock">Time source used for deterministic sampling and deadlines.</param>
/// <param name="archive">Provides the deployment archive? boundary used by this operation.</param>
/// <param name="retainedLogs">Provides the deployment log query? boundary used by this operation.</param>
public sealed class DeploymentAnalysisContextBuilder(
    AutoMateDbContext db,
    IDeploymentDiagnosticStore diagnostics,
    IDeploymentMetricQuery metrics,
    IDiagnosticRedactor redactor,
    IOptions<AiAnalysisOptions> options,
    IOptions<TelemetryStorageOptions> storage,
    TimeProvider clock,
    IDeploymentArchive? archive = null,
    IDeploymentLogQuery? retainedLogs = null) : IDeploymentAnalysisContextBuilder
{
    /// <summary>
    ///     Reads a bounded deployment-scoped diagnostic window with explicit omission/availability
    ///     metadata.
    /// </summary>
    /// <param name="deploymentId">The exact deployment identity used for ownership or correlation.</param>
    /// <param name="token">Cancels the operation when supported by the underlying work.</param>
    /// <returns>
    ///     A task returning bounded redacted evidence text, permitted references and collection
    ///     provenance.
    /// </returns>
    public async Task<DeploymentAnalysisContext> BuildAsync(Guid deploymentId, CancellationToken token = default)
    {
        if (archive is not null) return await BuildAsync(deploymentId, new AssessmentSelection(), token);

        // Compatibility for retained-data adapters without the richer archive selection port.
        var deployment = await db.Deployments.AsNoTracking().Where(d => d.Id == deploymentId).Select(d => new
        {
            Project = d.CsProject!.AppId,
            Owner = d.CsProject.Application.UserId,
            d.CreatedAt
        }).SingleOrDefaultAsync(token);

        if (deployment is null) return new DeploymentAnalysisContext("", []);
        var history = await diagnostics.ReadRecentAsync(deployment.Project, deploymentId,
            AnalysisContextSelector.MaximumCandidates, token);

        history = history with
        {
            Events =
            [
                .. history.Events.Where(e => e.ProjectId == deployment.Project &&
                                             (e.DeploymentId == deploymentId || e.DeploymentId is null))
            ]
        };

        var unavailable = storage.Value is { ManagedService: true, ManagedDataProcessingApproved: false };
        IReadOnlyList<DeploymentMetricPoint> points = [];
        if (unavailable)
            return new AnalysisContextSelector(redactor)
                .Select(history, points,
                    AnalysisContextBudget.From(options.Value), unavailable, token: token);

        try
        {
            var now = clock.GetUtcNow();
            var end = history.Events.Where(e => e.TimestampUtc <= now).Max(e => e.TimestampUtc) ?? now;
            var start = end.AddHours(-1);
            if (start < deployment.CreatedAt) start = deployment.CreatedAt;
            if (start < now.AddDays(-30)) start = now.AddDays(-30);
            if (start < end)
                points = await metrics.ReadAsync(deployment.Owner, deployment.Project, deploymentId, start, end,
                    300, token);
        }
        catch (Exception) when (!token.IsCancellationRequested)
        {
            unavailable = true;
        }

        return new AnalysisContextSelector(redactor)
            .Select(history, points, AnalysisContextBudget.From(options.Value), unavailable, token: token);
    }

    /// <summary>
    ///     Collects an explicitly selected context; default supports existing test/provider
    ///     implementations.
    /// </summary>
    /// <param name="deploymentId">The exact deployment identity used for ownership or correlation.</param>
    /// <param name="selection">The immutable source, container, focus and time-range options.</param>
    /// <param name="token">Cancels the operation when supported by the underlying work.</param>
    /// <returns>
    ///     A task returning bounded redacted evidence text, permitted references and collection
    ///     provenance.
    /// </returns>
    public async Task<DeploymentAnalysisContext> BuildAsync(Guid deploymentId, AssessmentSelection selection,
        CancellationToken token = default)
    {
        selection = selection.Normalize();
        var deployment = await db.Deployments
            .AsNoTracking()
            .Where(d => d.Id == deploymentId)
            .Select(d => new
                {
                    Project = d.CsProject!.AppId,
                    Owner = d.CsProject.Application.UserId,
                    d.CreatedAt,
                    d.UpdatedAt,
                    d.Status,
                    d.Outcome,
                    d.ConfigurationSnapshotJson
                }
            ).SingleOrDefaultAsync(token);

        if (deployment is null) return new DeploymentAnalysisContext("", []);

        var now = clock.GetUtcNow();
        var window = selection.Window(deployment.Status, deployment.CreatedAt, deployment.UpdatedAt, now);
        var provenance = new AssessmentProvenance(selection, selection.Resolve(deployment.Status),
            deployment.Status, deployment.Outcome, now, window);

        if (window.End <= window.Start ||
            storage.Value is { ManagedService: true, ManagedDataProcessingApproved: false })
            return new DeploymentAnalysisContext("", [], provenance);

        var query = new ArchiveAssessmentQuery(deployment.Owner, deployment.Project, deploymentId, selection, window);
        var page = await AssessmentEvidenceReader.ReadAsync(db, archive, query, token, retainedLogs);

        IReadOnlyList<DeploymentMetricPoint> points = [];
        var unavailable = false;

        if (selection.IncludeMetrics)
            try
            {
                points = archive switch
                {
                    null => await metrics.ReadAssessmentAsync(query, token),
                    IDeploymentAssessmentArchive selectedArchive => await selectedArchive.ReadAssessmentMetricsAsync(
                        query, token),
                    _ => await archive.ReadAssessmentMetricsAsync(query, token)
                };

                if (archive is not null && points.Count == 0)
                    points = await metrics.ReadAssessmentAsync(query, token);
            }
            catch (Exception) when (!token.IsCancellationRequested)
            {
                unavailable = true;
            }

        var history = new DeploymentTerminalHistory([
                .. page.Events.Select(e => redactor.RedactTerminal(
                    DeploymentTerminalLog.FromEvent(e.OrderId, e.Event, e.Channel!)))
            ],
            page.EarlierOmitted, page.Availability);

        object? configuration = null;
        if (deployment.ConfigurationSnapshotJson is { } json)
            try
            {
                var snapshot = JsonSerializer.Deserialize<DeploymentConfigurationSnapshot>(json);
                if (snapshot is not null)
                    configuration = new
                    {
                        provider = Bounded(snapshot.Provider),
                        runtime = Bounded(snapshot.Runtime),
                        environment = Bounded(snapshot.Environment),
                        snapshot.Port,
                        snapshot.Public,
                        region = Bounded(snapshot.Region),
                        image = Bounded(snapshot.Image)
                    };
            }
            catch (JsonException)
            {
            }

        var metadata = new
        {
            status = deployment.Status.ToString(),
            outcome = deployment.Outcome.ToString(),
            assessment = provenance.EffectiveKind.ToString(),
            collectedAt = now,
            window,
            configuration,
            channels = page.Channels.Where(c => page.Events.Any(e => e.Channel == c.Channel)).Take(24).Select(c => new
            {
                channel = "channel-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(c.Channel)))[..12],
                source = c.Source.ToString()
            }).ToArray()
        };

        return new AnalysisContextSelector(redactor).Select(history, points, AnalysisContextBudget.From(options.Value),
                unavailable, provenance.EffectiveKind, metadata, token) with
            {
                Provenance = provenance
            };
    }

    /// <summary>
    ///     Keep recorded configuration small enough to retain status and range metadata in the
    ///     input budget.
    /// </summary>
    /// <param name="value">The value to normalize, redact or validate.</param>
    /// <returns>The bounded derived from the validated input, or null when no value is available.</returns>
    private static string? Bounded(string? value)
    {
        return value is { Length: > 128 } ? value[..128] : value;
    }
}
