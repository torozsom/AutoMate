using System.Globalization;
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;

namespace Web.Components.Shared.Metrics;

/// <summary>
///     Presents observed SVG metric points with accessible pointer and keyboard inspection.
/// </summary>
public partial class TelemetryChart
{
    /// <summary>Imported local interaction module.</summary>
    private IJSObjectReference? _module;

    /// <summary>DOM host for local pointer and keyboard interaction.</summary>
    private ElementReference _root;

    /// <summary>Human-readable chart heading.</summary>
    [Parameter]
    public string Title { get; set; } = "";

    /// <summary>Metric key determining value units.</summary>
    [Parameter]
    public string Metric { get; set; } = "";

    /// <summary>Observed points; missing intervals are not included.</summary>
    [Parameter]
    public IReadOnlyList<TelemetryChartPoint> Points { get; set; } = [];

    /// <summary>Inclusive displayed interval start.</summary>
    [Parameter]
    public DateTimeOffset Start { get; set; }

    /// <summary>Displayed interval end.</summary>
    [Parameter]
    public DateTimeOffset End { get; set; }

    /// <summary>Maximum contiguous observation spacing.</summary>
    [Parameter]
    public TimeSpan Gap { get; set; } = TimeSpan.FromDays(1);

    /// <summary>Minute windows show time; long windows include the calendar year.</summary>
    private string AxisFormat => End - Start <= TimeSpan.FromDays(1) ? "HH:mm" :
        End - Start > TimeSpan.FromDays(90) ? "MMM yyyy" : "MMM d";

    /// <summary>
    ///     Detaches chart inspection handlers and releases its JS module.
    /// </summary>
    /// <returns>A task representing completion of component cleanup.</returns>
    public async ValueTask DisposeAsync()
    {
        if (_module is null) return;
        try
        {
            await _module.InvokeVoidAsync("detach", _root);
            await _module.DisposeAsync();
        }
        catch (JSDisconnectedException)
        {
        }
    }

    /// <summary>
    ///     Maps a timestamp to a padded chart coordinate.
    /// </summary>
    /// <param name="timestamp">Recorded timestamp, if available.</param>
    /// <returns>The timestamp position in the padded SVG plotting interval.</returns>
    private double X(DateTimeOffset timestamp)
    {
        return 105 + 345 * Math.Clamp((timestamp - Start).TotalSeconds / Math.Max(1, (End - Start).TotalSeconds), 0, 1);
    }

    /// <summary>
    ///     Produces culture-independent SVG numeric attributes.
    /// </summary>
    /// <param name="number">SVG coordinate or finite numeric value.</param>
    /// <returns>The invariant-culture SVG numeric text.</returns>
    private static string Number(double number)
    {
        return number.ToString("0.###", CultureInfo.InvariantCulture);
    }

    /// <summary>
    ///     Accessible observation details shared by tooltips and keyboard inspection.
    /// </summary>
    /// <param name="point">Observed chart point to describe.</param>
    /// <returns>Accessible timestamp, mean and available extrema text for the observation.</returns>
    private string Detail(TelemetryChartPoint point)
    {
        return
            $"{point.Timestamp:u} · Average {TelemetryPresentation.Format(point.Average, Metric)} · Min {TelemetryPresentation.Format(point.Minimum, Metric)} · Max {TelemetryPresentation.Format(point.Maximum, Metric)}";
    }

    /// <summary>
    ///     Attaches chart pointer and keyboard inspection after interactive rendering.
    /// </summary>
    /// <param name="firstRender">Whether this is the first interactive render.</param>
    /// <returns>A task representing completion of this operation, including any awaited callbacks.</returns>
    /// <exception cref="Microsoft.JSInterop.JSException">
    ///     The browser interaction fails or the interactive circuit is
    ///     disconnected.
    /// </exception>
    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (!firstRender) return;
        _module = await Js.InvokeAsync<IJSObjectReference>("import", "./js/telemetry-chart.js");
        await _module.InvokeVoidAsync("attach", _root);
    }
}
