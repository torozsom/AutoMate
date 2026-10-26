using System.Globalization;
using Application.Data.Apps;
using Microsoft.AspNetCore.Components;
using Web.Components.Shared.Metrics;

namespace Web.Components.Shared.Workspace;

/// <summary>
///     Presents recorded deployment activity and resource aggregates with numeric alternatives.
/// </summary>
public partial class WorkspaceOverviewView
{
    /// <summary>Independent numeric alternative page state.</summary>
    private int _activityPage = 1, _resourcePage = 1, _activitySize = 25, _resourceSize = 25;

    /// <summary>Resets table pages when the frozen statistics window changes.</summary>
    private DateTimeOffset _lastStart, _lastEnd;


    /// <summary>Authorized immutable workspace read model.</summary>
    [Parameter]
    public WorkspaceOverview? Data { get; set; }

    /// <summary>Known terminal outcomes form the success-rate denominator.</summary>
    private string SuccessRate => Data is { } d && d.Succeeded + d.Failed > 0
        ? $"{100d * d.Succeeded / (d.Succeeded + d.Failed):0.#}%"
        : "—";

    /// <summary>
    ///     Resets numeric-table pages when the overview time window changes.
    /// </summary>
    protected override void OnParametersSet()
    {
        if (_lastStart != Data?.Start || _lastEnd != Data?.End)
        {
            _activityPage = _resourcePage = 1;
            _lastStart = Data?.Start ?? default;
            _lastEnd = Data?.End ?? default;
        }
    }

    /// <summary>
    ///     Scales a bar against the largest recorded daily total.
    /// </summary>
    /// <param name="count">Recorded deployment total used to size the bar.</param>
    /// <returns>CSS positioning or size text derived from the current display state.</returns>
    private string Height(int count)
    {
        return $"height:{(160d * count / Math.Max(1, Data!.Activity
                .Max(d => d.Succeeded + d.Failed + d.Unknown)))
            .ToString("0.##", CultureInfo.InvariantCulture)}px";
    }

    /// <summary>
    ///     Builds chart points only for observed days.
    /// </summary>
    /// <param name="metric">Stored metric series name.</param>
    /// <returns>Observed chart points in timestamp order, without synthetic missing samples.</returns>
    private IReadOnlyList<TelemetryChartPoint> Points(string metric)
    {
        return
        [
            .. Data!.Resources
                .Where(r => r.Metric == metric)
                .Select(r => new TelemetryChartPoint(r.Day, r.Average, r.Minimum, r.Maximum))
        ];
    }

    /// <summary>
    ///     Links the selected deployment, or its project before launch.
    /// </summary>
    /// <param name="project">Project identity associated with this operation.</param>
    /// <param name="deployment">Deployment identity or metadata to inspect, if available.</param>
    /// <returns>The deployment-history route when an identity exists; otherwise the project route.</returns>
    private static string Link(Guid project, Guid? deployment)
    {
        return deployment is { } id ? $"/project/{project}/deployment/{id}" : $"/project/{project}";
    }

    /// <summary>
    ///     Unavailable durations are not represented as zero.
    /// </summary>
    /// <param name="seconds">Recorded duration in seconds, or null when unavailable.</param>
    /// <returns>The duration formatted as hours, minutes and seconds, or "Not recorded" when absent.</returns>
    private static string Duration(double? seconds)
    {
        return seconds is { } value ? TimeSpan.FromSeconds(value).ToString(@"hh\:mm\:ss") : "Not recorded";
    }
}
