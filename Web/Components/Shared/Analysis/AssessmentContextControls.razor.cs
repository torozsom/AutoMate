using System.Globalization;
using Application.Abstractions.Ai.Context;
using Application.Ai.Context;
using Domain.Enums.Deployments;
using Microsoft.AspNetCore.Components;

namespace Web.Components.Shared;

/// <summary>
///     Edits immutable analysis choices for sources, containers, metrics, focus and UTC time bounds.
/// </summary>
public partial class AssessmentContextControls
{
    /// <summary>Fixed finite source families displayed in a consistent order.</summary>
    private static readonly AssessmentSources[] Sources =
    [
        AssessmentSources.Build, AssessmentSources.Web, AssessmentSources.Database,
        AssessmentSources.GitHub, AssessmentSources.Azure, AssessmentSources.Deployment, AssessmentSources.Other
    ];

    /// <summary>Editable owner choices; the presenter stores a separate immutable submission.</summary>
    [Parameter]
    public AssessmentSelection Selection { get; set; } = new();

    /// <summary>Current persisted status used for a preview of Automatic resolution.</summary>
    [Parameter]
    public DeploymentStatus Status { get; set; }

    /// <summary>Recorded source/channel catalog, never inferred from log prose.</summary>
    [Parameter]
    public IReadOnlyList<AssessmentChannel> Channels { get; set; } = [];

    /// <summary>Recorded metric identities selectable independently of log channels.</summary>
    [Parameter]
    public IReadOnlyList<string> MetricContainers { get; set; } = [];

    /// <summary>Sources unsupported by the recorded deployment provider and source snapshot.</summary>
    [Parameter]
    public AssessmentSources UnsupportedSources { get; set; }

    /// <summary>Freezes every control while admission or execution is pending.</summary>
    [Parameter]
    public bool Disabled { get; set; }

    /// <summary>Returns a new draft without modifying an existing result or submitted options.</summary>
    [Parameter]
    public EventCallback<AssessmentSelection> OnChange { get; set; }

    /// <summary>
    ///     Owner-facing labels for the finite focus choices.
    /// </summary>
    /// <param name="kind">Assessment focus selected by the user.</param>
    /// <returns>The display label for the selected assessment focus.</returns>
    public static string FocusLabel(AssessmentKind kind)
    {
        return kind switch
        {
            AssessmentKind.Startup => "Startup assessment",
            AssessmentKind.FailureDiagnosis => "Failure diagnosis",
            AssessmentKind.RuntimeOverview => "Runtime overview",
            AssessmentKind.HistoricalReview => "Historical review", _ => "Automatic"
        };
    }

    /// <summary>
    ///     Owner-facing labels that distinguish application output from Azure platform events.
    /// </summary>
    /// <param name="source">Selected diagnostic source flag or JSON object.</param>
    /// <returns>The display label for the selected diagnostic source.</returns>
    public static string SourceLabel(AssessmentSources source)
    {
        return source switch
        {
            AssessmentSources.Build => "Container build",
            AssessmentSources.Web => "Web server", AssessmentSources.Database => "Database server",
            AssessmentSources.GitHub => "GitHub Actions",
            AssessmentSources.Azure => "Azure platform/system", AssessmentSources.Deployment => "Deployment events",
            _ => "Other / unknown"
        };
    }

    /// <summary>
    ///     Suppresses stale control events while the submission is frozen.
    /// </summary>
    /// <param name="value">Replacement immutable assessment choices.</param>
    /// <returns>A task representing completion of this operation, including any awaited callbacks.</returns>
    private Task Emit(AssessmentSelection value)
    {
        return Disabled ? Task.CompletedTask : OnChange.InvokeAsync(value);
    }

    /// <summary>
    ///     Changes emphasis without changing the persisted status supplied to the model.
    /// </summary>
    /// <param name="e">Browser change or keyboard event.</param>
    /// <returns>A task representing completion of this operation, including any awaited callbacks.</returns>
    /// <exception cref="FormatException">The change value is not an integer focus identifier.</exception>
    /// <exception cref="OverflowException">The supplied focus identifier exceeds the integer range.</exception>
    private Task KindChanged(ChangeEventArgs e)
    {
        return Emit(Selection with { Kind = (AssessmentKind)int.Parse(e.Value!.ToString()!) });
    }

    /// <summary>
    ///     Initializes UTC custom bounds or clears dates for a relative range.
    /// </summary>
    /// <param name="e">Browser change or keyboard event.</param>
    /// <returns>A task representing completion of this operation, including any awaited callbacks.</returns>
    /// <exception cref="FormatException">The change value is not an integer range identifier.</exception>
    /// <exception cref="OverflowException">The supplied range identifier exceeds the integer range.</exception>
    private Task RangeChanged(ChangeEventArgs e)
    {
        var range = (AssessmentRange)int.Parse(e.Value!.ToString()!);
        return Emit(Selection with
        {
            Range = range, Start = range == AssessmentRange.Custom ? DateTimeOffset.UtcNow.AddHours(-1) : null,
            End = range == AssessmentRange.Custom ? DateTimeOffset.UtcNow : null
        });
    }

    /// <summary>
    ///     Changes one source flag independently of metrics.
    /// </summary>
    /// <param name="source">Selected diagnostic source flag or JSON object.</param>
    /// <param name="e">Browser change or keyboard event.</param>
    /// <returns>A task representing completion of this operation, including any awaited callbacks.</returns>
    private Task SourceChanged(AssessmentSources source, ChangeEventArgs e)
    {
        return Emit(Selection with
        {
            Sources = (bool)e.Value! ? Selection.Sources | source : Selection.Sources & ~source
        });
    }

    /// <summary>
    ///     Enables or excludes numeric evidence independently of log selection.
    /// </summary>
    /// <param name="e">Browser change or keyboard event.</param>
    /// <returns>A task representing completion of this operation, including any awaited callbacks.</returns>
    private Task MetricsChanged(ChangeEventArgs e)
    {
        return Emit(Selection with { IncludeMetrics = (bool)e.Value! });
    }

    /// <summary>
    ///     Replaces all-channel selection with explicit recorded web/database channels.
    /// </summary>
    /// <param name="channel">Recorded log channel to select.</param>
    /// <param name="e">Browser change or keyboard event.</param>
    /// <returns>A task representing completion of this operation, including any awaited callbacks.</returns>
    private Task LogContainerChanged(string channel, ChangeEventArgs e)
    {
        return Emit(Selection with
        {
            LogContainers = Toggle(Selection.LogContainers, Channels
                .Where(c => c.Source is AssessmentSources.Web or AssessmentSources.Database)
                .Select(c => c.Channel), channel, (bool)e.Value!)
        });
    }

    /// <summary>
    ///     Replaces all-container selection with explicit recorded metric identities.
    /// </summary>
    /// <param name="container">Recorded container identity to select.</param>
    /// <param name="e">Browser change or keyboard event.</param>
    /// <returns>A task representing completion of this operation, including any awaited callbacks.</returns>
    private Task MetricContainerChanged(string container, ChangeEventArgs e)
    {
        return Emit(Selection with
        {
            MetricContainers = Toggle(Selection.MetricContainers, MetricContainers, container, (bool)e.Value!)
        });
    }

    /// <summary>
    ///     Keeps null as all until an individual checkbox changes, then emits sorted identities.
    /// </summary>
    /// <param name="selected">Current explicit selection, or null for all available items.</param>
    /// <param name="available">Current authorized selection catalog.</param>
    /// <param name="name">Metric or configuration name to resolve.</param>
    /// <param name="enabled">Whether to include the option.</param>
    /// <returns>Distinct selected identities sorted with ordinal comparison.</returns>
    private static string[] Toggle(string[]? selected, IEnumerable<string> available, string name, bool enabled)
    {
        var values = new HashSet<string>(selected ?? [.. available], StringComparer.Ordinal);
        if (enabled) values.Add(name);
        else values.Remove(name);
        return [.. values.Order(StringComparer.Ordinal)];
    }

    /// <summary>
    ///     Formats an absolute UTC instant for an unlabeled-zone browser datetime control.
    /// </summary>
    /// <param name="value">Optional UTC time to format.</param>
    /// <returns>UTC datetime-control text, or an empty string when the value is absent.</returns>
    private static string UtcValue(DateTimeOffset? value)
    {
        return value?.UtcDateTime.ToString("yyyy-MM-ddTHH:mm") ?? "";
    }

    /// <summary>
    ///     Interprets the explicitly UTC-labeled input without applying local time conversion.
    /// </summary>
    /// <param name="value">Raw change-event value to parse.</param>
    /// <returns>The interpreted UTC timestamp, or null when parsing fails.</returns>
    private static DateTimeOffset? ParseUtc(object? value)
    {
        return DateTime.TryParse(value?.ToString(), CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var time)
            ? new DateTimeOffset(time)
            : null;
    }

    /// <summary>
    ///     Changes the custom UTC start; server admission validates the completed range.
    /// </summary>
    /// <param name="e">Browser change or keyboard event.</param>
    /// <returns>A task representing completion of this operation, including any awaited callbacks.</returns>
    private Task StartChanged(ChangeEventArgs e)
    {
        return Emit(Selection with { Start = ParseUtc(e.Value) });
    }

    /// <summary>
    ///     Changes the custom UTC end; future and oversized ranges are rejected by admission.
    /// </summary>
    /// <param name="e">Browser change or keyboard event.</param>
    /// <returns>A task representing completion of this operation, including any awaited callbacks.</returns>
    private Task EndChanged(ChangeEventArgs e)
    {
        return Emit(Selection with { End = ParseUtc(e.Value) });
    }
}
