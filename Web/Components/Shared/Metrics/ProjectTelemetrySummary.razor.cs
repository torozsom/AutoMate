using Application.Abstractions.Diagnostics.Metrics;
using Microsoft.AspNetCore.Components;

namespace Web.Components.Shared.Metrics;

/// <summary>
///     Loads and presents owner-scoped project aggregates for the selected metric window.
/// </summary>
public partial class ProjectTelemetrySummary
{
    /// <summary>Cancels requests on navigation.</summary>
    private readonly CancellationTokenSource _lifetime = new();

    /// <summary>Indicates an independently scoped statistics request.</summary>
    private bool _busy;

    /// <summary>Cancellation source for the most recently requested period.</summary>
    private CancellationTokenSource? _load;

    /// <summary>Loaded project identity.</summary>
    private Guid _loaded;

    /// <summary>Loaded owner identity.</summary>
    private Guid _loadedOwner;

    /// <summary>Fixed unavailable or partial-data guidance.</summary>
    private string? _notice;

    /// <summary>Frozen range selected by the shared UTC controls.</summary>
    private MetricTimeRange _range = MetricTimeRange.Preset("30d", DateTimeOffset.UtcNow);

    /// <summary>Bounds shared summary and chart time ranges.</summary>
    private DateTimeOffset _start, _end;

    /// <summary>Last accepted owner/project/range projection.</summary>
    private ProjectTelemetryAnalytics? _summary;

    /// <summary>Authorized project whose daily statistics are displayed.</summary>
    [Parameter]
    public Guid ProjectId { get; set; }

    /// <summary>The parent page's resolved owner; the analytics service still verifies project ownership.</summary>
    [Parameter]
    public Guid UserId { get; set; }

    /// <summary>Compatible legacy summaries retain their recorded container identity.</summary>
    private IReadOnlyList<MetricObservation> Observations
        => _summary?.Observations?.Chart ??
           _summary?.Daily
               .Where(p => p.Average is not null)
               .Select(p => new MetricObservation(ProjectId, p.DeploymentId,
                   p.Container, p.Metric, p.Unit, p.DayUtc, p.SampleCount,
                   p.Average!.Value * Math.Max(1, p.SampleCount),
                   p.Minimum ?? p.Average.Value, p.Maximum ?? p.Average.Value, p.Incomplete)).ToArray() ?? [];

    /// <summary>
    ///     Cancels pending analytic reads and releases owned cancellation sources.
    /// </summary>
    public void Dispose()
    {
        _lifetime.Cancel();
        _lifetime.Dispose();
    }

    /// <summary>
    ///     Changes the range without rewriting saved analysis preferences.
    /// </summary>
    /// <param name="range">Validated UTC metric window selected by the user.</param>
    /// <returns>A task representing completion of this operation, including any awaited callbacks.</returns>
    private async Task RangeChangedAsync(MetricTimeRange range)
    {
        _range = range;
        await LoadAsync();
    }

    /// <summary>
    ///     Formats a sample-weighted project resource average.
    /// </summary>
    /// <param name="metric">Stored metric series name.</param>
    /// <returns>The formatted average, including the documented fallback when data is absent.</returns>
    private string Average(string metric)
    {
        var points = Observations.Where(p => p.Metric == metric).ToArray();
        var raw = points.Where(p => p.Samples > 0).ToArray();
        var values = raw.Length > 0 ? raw : points;

        return TelemetryPresentation.Format(values.Length == 0
            ? null
            : values.Sum(p => p.Sum) /
              Math.Max(1, values.Sum(p => p.Samples > 0 ? p.Samples : p.ImportedIntervals)), metric);
    }

    /// <summary>
    ///     Reloads analytics when the supplied owner or project identity changes.
    /// </summary>
    /// <returns>A task representing completion of this operation, including any awaited callbacks.</returns>
    protected override async Task OnParametersSetAsync()
    {
        if (_loaded == ProjectId && _loadedOwner == UserId)
            return;

        _loadedOwner = UserId;
        _summary = null;
        _loaded = ProjectId;

        await LoadAsync();
    }

    /// <summary>
    ///     Loads an independently scoped period and discards cancelled or obsolete responses.
    /// </summary>
    /// <returns>A task representing completion of this operation, including any awaited callbacks.</returns>
    private async Task LoadAsync()
    {
        if (_load is { } previousRequest) await previousRequest.CancelAsync();

        using var request = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _load = request;
        _busy = true;

        try
        {
            // Blazor children may load while their parent is querying; never share the circuit's EF context.
            await using var scope = ServiceScopes.CreateAsyncScope();
            var analytics = scope.ServiceProvider.GetRequiredService<IProjectTelemetryAnalytics>();
            var now = _range.End;
            var start = _range.Start;
            var owner = UserId;
            var project = ProjectId;
            var result = await analytics.ReadAsync(owner, project, start, now, request.Token);

            if (request.IsCancellationRequested || owner != UserId || project != ProjectId)
                return;

            _start = start;
            _end = now;
            _summary = result;
            _notice = _summary.Availability;
        }
        catch (OperationCanceledException) when (request.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            if (request.IsCancellationRequested) return;
            _summary = null;
            _notice = "Project analytics are temporarily unavailable.";
            Logger.LogWarning("Project analytics unavailable: {FailureType}.", ex.GetType().Name);
        }
        finally
        {
            if (ReferenceEquals(_load, request))
            {
                _busy = false;
                _load = null;
            }
        }
    }
}
