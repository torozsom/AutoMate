using Application.Abstractions.Diagnostics.Events;
using Application.Abstractions.Diagnostics.Metrics;
using Application.Data.Apps;
using Application.Data.Users;
using Domain.Entities.Deployments;
using Domain.Enums.Deployments;
using Microsoft.AspNetCore.Components;
using Web.Components.Pages.Identity;
using Web.Components.Shared.Console;
using Web.Components.Shared.Metrics;

namespace Web.Components.Pages;

/// <summary>
///     Presents owner-authorized saved deployment output, selected metrics and inline AI analysis.
/// </summary>
public partial class DeploymentHistory
{
    /// <summary>Cancels previous range requests and all requests on navigation.</summary>
    private readonly CancellationTokenSource _lifetime = new();

    /// <summary>Includes retained older deployments by default, with bounded zoom choices.</summary>
    private readonly int _metricDays = 0;

    /// <summary>Current exact-project egress consent; historical settings do not grant consent.</summary>
    private bool _aiConsented;

    /// <summary>Server-authorized page state.</summary>
    private bool _authorized;

    /// <summary>Suppresses overlapping history reads.</summary>
    private bool _busy;

    /// <summary>Selected original terminal channel.</summary>
    private string _channel = "";

    /// <summary>Atomic memoized projection keyed by response identity and selected container.</summary>
    private (IReadOnlyList<MetricObservation>? Source, string Container, IReadOnlyList<MetricObservation> Items)
        _containerCache = (null, "", []);

    /// <summary>Owner-authorized metadata for this exact history route.</summary>
    private Deployment? _deployment;

    /// <summary>Opaque cursor for the preceding saved log page.</summary>
    private string? _earlierCursor;

    /// <summary>Fixed chart time range.</summary>
    private DateTimeOffset _end = DateTimeOffset.UtcNow;

    /// <summary>Safe history failure message.</summary>
    private string? _error;

    /// <summary>Bounded current output page.</summary>
    private DeploymentTerminalHistory? _history;

    /// <summary>Preserves the selected page/channel across unchanged route parameters.</summary>
    private (Guid Project, Guid Deployment)? _loadedRoute;

    /// <summary>The active log request, independent of metrics.</summary>
    private CancellationTokenSource? _logLoad;

    /// <summary>Distinguishes missing data from unavailable or truncated history.</summary>
    private string? _metricAvailability;

    /// <summary>Independent metric request state; log paging never resets it.</summary>
    private bool _metricBusy;

    /// <summary>Selected container, shared by all metric summaries and charts.</summary>
    private string _metricContainer = "";

    /// <summary>Selected and displayed metric window ends do not change the recorded deployment activity window.</summary>
    private DateTimeOffset _metricEnd = DateTimeOffset.UtcNow;

    /// <summary>The current range request identity.</summary>
    private CancellationTokenSource? _metricLoad;

    /// <summary>Displayed metric range beginning.</summary>
    private DateTimeOffset _metricStart = DateTimeOffset.UtcNow.AddDays(-30);

    /// <summary>Bounded metric aggregates.</summary>
    private IReadOnlyList<DeploymentMetricPoint> _metrics = [];

    /// <summary>Detached recorded observations, preserving counts and provenance.</summary>
    private IReadOnlyList<MetricObservation> _observations = [];

    /// <summary>Internal owner identity.</summary>
    private Guid _owner;

    /// <summary>Frozen current chart selection.</summary>
    private MetricTimeRange? _requestedRange;

    /// <summary>User-entered bounded history search text.</summary>
    private string _search = "";

    /// <summary>Recorded deployment beginning, independent of the current date.</summary>
    private DateTimeOffset _start = DateTimeOffset.UtcNow.AddDays(-30);

    /// <summary>Terminal presentation sink.</summary>
    private Terminal? _terminal;

    /// <summary>Authorized project identity.</summary>
    [Parameter]
    public Guid ProjectId { get; set; }

    /// <summary>Selected historical run identity.</summary>
    [Parameter]
    public Guid DeploymentId { get; set; }

    /// <summary>Changes the data identity only when the container or observation response changes.</summary>
    private IReadOnlyList<MetricObservation> ContainerObservations
    {
        get
        {
            if (!ReferenceEquals(_containerCache.Source, _observations) ||
                _containerCache.Container != _metricContainer)
                _containerCache = (_observations, _metricContainer,
                    [.. _observations.Where(p => p.Container == _metricContainer)]);

            return _containerCache.Items;
        }
    }

    /// <summary>
    ///     Cancels outstanding history and metric reads and releases the page lifetime source.
    /// </summary>
    public void Dispose()
    {
        _lifetime.Cancel();
        _lifetime.Dispose();
    }

    /// <summary>
    ///     Loads a validated selection while retaining the saved terminal state.
    /// </summary>
    /// <param name="range">Validated UTC metric window selected by the user.</param>
    /// <returns>A task representing completion of this operation, including any awaited callbacks.</returns>
    private async Task MetricRangeChangedAsync(MetricTimeRange range)
    {
        _requestedRange = range;
        await LoadMetricsAsync();
    }

    /// <summary>
    ///     Orders the selected container's observations for a metric.
    /// </summary>
    /// <param name="name">Metric or configuration name to resolve.</param>
    /// <returns>Selected container metric points ordered by timestamp.</returns>
    private IEnumerable<DeploymentMetricPoint> Selected(string name)
    {
        return _metrics.Where(p => p.Container == _metricContainer && p.Name == name).OrderBy(p => p.Timestamp);
    }

    /// <summary>
    ///     Shows interval means or extrema, without claiming raw sample weighting.
    /// </summary>
    /// <param name="metric">Stored metric series name.</param>
    /// <param name="peak">Whether to display the observed maximum instead of the mean.</param>
    /// <returns>The formatted value, including the documented fallback when data is absent.</returns>
    private string Value(string metric, bool peak)
    {
        var points = Selected(metric).ToArray();
        return TelemetryPresentation.Format(
            points.Length == 0 ? null : peak ? points.Max(p => p.Maximum) : ObservedAverage(metric), metric);
    }

    /// <summary>
    ///     Raw samples are weighted by their recorded count; imported intervals stay separate.
    /// </summary>
    /// <param name="metric">Stored metric series name.</param>
    /// <returns>The sample-weighted mean, falling back to imported interval weighting; zero for an empty selection.</returns>
    private double ObservedAverage(string metric)
    {
        var points = _observations.Where(p => p.Container == _metricContainer && p.Metric == metric).ToArray();
        var raw = points.Where(p => p.Samples > 0).ToArray();
        var selected = raw.Length > 0 ? raw : points;
        return selected.Sum(p => p.Sum) /
               Math.Max(1, selected.Sum(p => p.Samples > 0 ? p.Samples : p.ImportedIntervals));
    }

    /// <summary>
    ///     Loads the selected owned deployment and resets history, metrics and consent when the route
    ///     changes.
    /// </summary>
    /// <returns>A task representing completion of this operation, including any awaited callbacks.</returns>
    protected override async Task OnParametersSetAsync()
    {
        if (_loadedRoute == (ProjectId, DeploymentId)) return;
        var selection = (ProjectId, DeploymentId);
        _loadedRoute = selection;
        _history = null;
        if (_logLoad is { } previousRequest) await previousRequest.CancelAsync();
        if (_metricLoad is { } previousMetricRequest) await previousMetricRequest.CancelAsync();
        _metrics = [];
        _observations = [];
        _requestedRange = null;
        _channel = "";
        _authorized = false;
        await using var ownerScope = ServiceScopes.CreateAsyncScope();
        var owner = await AuthenticatedUserResolver.GetCurrentUserIdAsync(Authentication,
            ownerScope.ServiceProvider.GetRequiredService<IUserService>());
        if (selection != (ProjectId, DeploymentId) || _lifetime.IsCancellationRequested) return;
        _owner = owner;
        _deployment = null;
        _aiConsented = false;
        await using (var scope = ServiceScopes.CreateAsyncScope())
        {
            var details = scope.ServiceProvider.GetService<IDeploymentDetailsService>();
            var deployment = details is null
                ? null
                : await details.GetAsync(owner, selection.ProjectId, selection.DeploymentId, _lifetime.Token);
            if (selection != (ProjectId, DeploymentId) || _lifetime.IsCancellationRequested) return;
            _deployment = deployment;
            if (_deployment is not null)
            {
                _start = _deployment.CreatedAt;
                _end = _deployment.Status == DeploymentStatus.Running ? DateTimeOffset.UtcNow : _deployment.UpdatedAt;
                if (_end <= _start) _end = _start.AddMinutes(1);
                var app = scope.ServiceProvider.GetService<IProjectActionQuery>() is { } metadata
                    ? await metadata.GetAsync(ProjectId, _owner, _lifetime.Token)
                    : await scope.ServiceProvider.GetRequiredService<IApplicationService>()
                        .GetAppByIdAsync(ProjectId, _owner, _lifetime.Token);
                if (selection != (ProjectId, DeploymentId) || _lifetime.IsCancellationRequested) return;
                _aiConsented = app?.CsProjects.FirstOrDefault(p => p.Id == _deployment.CsProjectId)?.Configuration
                    ?.AiDiagnosticEgressConsented == true;
            }
        }

        await LoadAsync(null);
        if (_authorized) await LoadMetricsAsync();
    }

    /// <summary>
    ///     Requests the preceding cursor page without accumulating unbounded terminal memory.
    /// </summary>
    /// <returns>A task representing completion of this operation, including any awaited callbacks.</returns>
    private Task EarlierAsync()
    {
        return LoadAsync(_earlierCursor);
    }

    /// <summary>
    ///     Returns to the most recent page.
    /// </summary>
    /// <returns>A task representing completion of this operation, including any awaited callbacks.</returns>
    private Task LatestAsync()
    {
        return LoadAsync(null);
    }

    /// <summary>
    ///     Loads authorized bounded output and metric history.
    /// </summary>
    /// <param name="cursor">Opaque history position, or null to read the latest page.</param>
    /// <returns>A task representing completion of this operation, including any awaited callbacks.</returns>
    private async Task LoadAsync(string? cursor)
    {
        if (_logLoad is { } previousRequest) await previousRequest.CancelAsync();
        using var request = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _logLoad = request;
        _busy = true;
        try
        {
            var route = (ProjectId, DeploymentId);
            await using var scope = ServiceScopes.CreateAsyncScope();
            var service = scope.ServiceProvider.GetRequiredService<IDeploymentHistoryService>();
            var page = await service.ReadLogsV2Async(_owner, ProjectId, DeploymentId, cursor, true, 500, _search,
                request.Token, _channel);
            if (route != (ProjectId, DeploymentId) || request.IsCancellationRequested) return;
            _earlierCursor = page.EarlierCursor;
            _history = new DeploymentTerminalHistory(page.Events, page.EarlierCursor is not null, page.Availability,
                page.CanAdvanceCursor);
            _authorized = true;
            _error = null;
            await RenderTerminalAsync();
        }
        catch (OperationCanceledException) when (request.IsCancellationRequested)
        {
        }
        catch (UnauthorizedAccessException)
        {
            if (request.IsCancellationRequested) return;
            _authorized = false;
            _error = "Deployment history access denied.";
        }
        catch (Exception ex)
        {
            if (request.IsCancellationRequested) return;
            Logger.LogWarning("Deployment history unavailable: {FailureType}.", ex.GetType().Name);
            _error = "Some deployment history is temporarily unavailable.";
        }
        finally
        {
            if (ReferenceEquals(_logLoad, request))
            {
                _busy = false;
                _logLoad = null;
            }
        }
    }

    /// <summary>
    ///     Restores the selected channel into xterm after its initialization.
    /// </summary>
    /// <returns>A task representing completion of this operation, including any awaited callbacks.</returns>
    private async Task RenderTerminalAsync()
    {
        if (_terminal is null || _history is null) return;
        await _terminal.ClearAsync();
        var output = string.Concat(_history.Events
            .Where(e => string.IsNullOrEmpty(_channel) || e.TerminalChannel == _channel)
            .Select(DeploymentTerminalPresentation.Format));
        await _terminal.WriteAsync(output.Length > 0 ? output : "[No saved output in this terminal page.]\r\n");
    }

    /// <summary>
    ///     Loads a cancellable metric range without reading or clearing saved terminal output.
    /// </summary>
    /// <returns>A task representing completion of this operation, including any awaited callbacks.</returns>
    private async Task LoadMetricsAsync()
    {
        if (_metricLoad is { } previousMetricRequest) await previousMetricRequest.CancelAsync();
        using var request = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _metricLoad = request;
        _metricBusy = true;
        var route = (ProjectId, DeploymentId);
        var days = _metricDays;
        try
        {
            await using var scope = ServiceScopes.CreateAsyncScope();
            var service = scope.ServiceProvider.GetRequiredService<IDeploymentHistoryService>();
            var rangeEnd = _requestedRange?.End ?? (_end < DateTimeOffset.UtcNow ? _end : DateTimeOffset.UtcNow);
            var rangeStart = _requestedRange?.Start ?? (days == 0 ? _start : rangeEnd.AddDays(-days));
            var truncatedLifetime = rangeStart < rangeEnd.AddYears(-5);
            if (truncatedLifetime) rangeStart = rangeEnd.AddYears(-5);
            if (rangeEnd - rangeStart < TimeSpan.FromMinutes(5)) rangeStart = rangeEnd.AddMinutes(-5);
            var exploration = scope.ServiceProvider.GetService<IMetricExploration>();
            DeploymentMetricHistory result;
            IReadOnlyList<MetricObservation> observations;
            if (exploration is not null)
            {
                var observed = await exploration.ReadAsync(
                    new MetricExplorationQuery(_owner, new MetricTimeRange(rangeStart, rangeEnd), route.ProjectId,
                        route.DeploymentId), request.Token);
                observations = observed.Chart;
                result = new DeploymentMetricHistory([
                    .. observations.Select(p => new DeploymentMetricPoint(p.Container, p.Metric, p.Unit,
                        p.Timestamp, p.Average, p.Minimum, p.Maximum))
                ], observed.Availability);
            }
            else
            {
                result = await service.ReadMetricsAsync(_owner, route.ProjectId, route.DeploymentId, rangeStart,
                    rangeEnd, 240, request.Token);
                var window = new MetricTimeRange(rangeStart, rangeEnd);
                observations =
                [
                    .. result.Points
                        .GroupBy(p => new { p.Container, p.Name, p.Unit, Time = window.Bucket(p.Timestamp) })
                        .Select(g => new MetricObservation(route.ProjectId, route.DeploymentId, g.Key.Container,
                            g.Key.Name,
                            g.Key.Unit, g.Key.Time, 0, g.Sum(p => p.Average), g.Min(p => p.Minimum),
                            g.Max(p => p.Maximum), false, g.Count()))
                ];
            }

            if (request.IsCancellationRequested || route != (ProjectId, DeploymentId)) return;
            _metrics = result.Points;
            _observations = observations;
            _metricEnd = rangeEnd;
            _metricStart = rangeStart;
            _metricAvailability = (result.Availability +
                                   (truncatedLifetime
                                       ? " Deployment lifetime is truncated to the most recent five years."
                                       : "")).Trim();
            if (_metricAvailability.Length == 0) _metricAvailability = null;
            if (_metrics.All(p => p.Container != _metricContainer))
                _metricContainer = _metrics.Select(p => p.Container).Distinct().Order().FirstOrDefault() ?? "";
        }
        catch (OperationCanceledException) when (request.IsCancellationRequested)
        {
        }
        catch (UnauthorizedAccessException)
        {
            if (request.IsCancellationRequested) return;
            _authorized = false;
            _metrics = [];
            _error = "Deployment history access denied.";
        }
        catch (Exception ex)
        {
            if (request.IsCancellationRequested) return;
            Logger.LogWarning("Metric history unavailable: {FailureType}.", ex.GetType().Name);
            _metrics = [];
            _metricAvailability = "Metric history is temporarily unavailable. Saved terminal output remains available.";
        }
        finally
        {
            if (ReferenceEquals(_metricLoad, request))
            {
                _metricBusy = false;
                _metricLoad = null;
            }
        }
    }
}
