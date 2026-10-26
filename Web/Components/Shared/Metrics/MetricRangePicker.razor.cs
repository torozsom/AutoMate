using Application.Abstractions.Diagnostics.Metrics;
using Microsoft.AspNetCore.Components;

namespace Web.Components.Shared.Metrics;

/// <summary>
///     Selects relative or explicit frozen UTC windows and validates range bounds before publication.
/// </summary>
public partial class MetricRangePicker
{
    /// <summary>Selectable relative windows.</summary>
    private static readonly KeyValuePair<string, string>[] Choices =
    [
        new("10m", "10 minutes"), new("30m", "30 minutes"), new("1h", "1 hour"), new("6h", "6 hours"),
        new("24h", "24 hours"), new("7d", "7 days"), new("30d", "30 days"), new("90d", "90 days"),
        new("1y", "1 year"), new("5y", "5 years")
    ];

    /// <summary>Stable label identities for multiple pickers.</summary>
    private readonly string _id = "range-" + Guid.NewGuid().ToString("N");

    /// <summary>Current preset or custom selection.</summary>
    private string _choice = "30d";

    /// <summary>Fixed validation or lifetime clipping guidance.</summary>
    private string? _notice;

    /// <summary>UTC wall clock input values, never browser-local conversions.</summary>
    private DateTime _start, _end;

    /// <summary>Initial selection, preserved independently on each chart group.</summary>
    [Parameter]
    public string Default { get; set; } = "30d";

    /// <summary>Recorded beginning for historical lifetime selection.</summary>
    [Parameter]
    public DateTimeOffset? LifetimeStart { get; set; }

    /// <summary>Recorded activity end; relative selections use the current clock.</summary>
    [Parameter]
    public DateTimeOffset? LifetimeEnd { get; set; }

    /// <summary>Frozen validated range passed to the parent query.</summary>
    [Parameter]
    public EventCallback<MetricTimeRange> Changed { get; set; }

    /// <summary>Accepted preset identity for relative automatic refresh.</summary>
    [Parameter]
    public EventCallback<string> SelectionChanged { get; set; }

    /// <summary>
    ///     Initializes the selected preset and a custom window covering the last 30 days.
    /// </summary>
    protected override void OnInitialized()
    {
        _choice = Default;
        var now = DateTimeOffset.UtcNow;
        _start = now.AddDays(-30).UtcDateTime;
        _end = now.UtcDateTime;
    }

    /// <summary>
    ///     Publishes relative windows immediately, while custom inputs await explicit validation.
    /// </summary>
    /// <param name="args">Browser event data for this interaction.</param>
    /// <returns>A task representing completion of this operation, including any awaited callbacks.</returns>
    private async Task ChooseAsync(ChangeEventArgs args)
    {
        _choice = args.Value?.ToString() ?? Default;
        _notice = null;
        if (_choice != "custom") await ApplyAsync();
    }

    /// <summary>
    ///     Validates a frozen UTC window and reports clipping explicitly.
    /// </summary>
    /// <returns>A task representing completion of this operation, including any awaited callbacks.</returns>
    private async Task ApplyAsync()
    {
        var now = DateTimeOffset.UtcNow;
        _notice = null;
        try
        {
            MetricTimeRange range;
            if (_choice == "custom")
            {
                range = new MetricTimeRange(new DateTimeOffset(DateTime.SpecifyKind(_start, DateTimeKind.Utc)),
                    new DateTimeOffset(DateTime.SpecifyKind(_end, DateTimeKind.Utc)));
            }
            else if (_choice == "lifetime")
            {
                var end = LifetimeEnd is { } finish && finish < now ? finish : now;
                var start = LifetimeStart ?? end.AddDays(-1);
                if (start < end.AddYears(-5))
                {
                    start = end.AddYears(-5);
                    _notice = "Deployment lifetime is truncated to the most recent five years.";
                }

                if (end - start < TimeSpan.FromMinutes(5))
                {
                    start = end.AddMinutes(-5);
                    _notice = "This recent deployment is shown within a five-minute observation window.";
                }

                range = new MetricTimeRange(start, end);
            }
            else
            {
                range = MetricTimeRange.Preset(_choice, now);
            }

            range.Validate(now);
            await SelectionChanged.InvokeAsync(_choice);
            await Changed.InvokeAsync(range);
        }
        catch (ArgumentException)
        {
            _notice = "Choose a UTC window from five minutes to five calendar years, ending no later than now.";
        }
    }
}
