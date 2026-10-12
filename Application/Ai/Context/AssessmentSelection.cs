using System.Text.Json;
using Application.Abstractions.Diagnostics;
using Domain.Enums;

namespace Application.Ai;

/// <summary>
///     Owner-selected focus; Automatic is resolved from persisted runtime status at collection
///     time.
/// </summary>
public enum AssessmentKind
{
    /// <summary>Resolves the focus from the deployment status when evidence is collected.</summary>
    Automatic,

    /// <summary>The startup assessment focus.</summary>
    Startup,

    /// <summary>The failure diagnosis assessment focus.</summary>
    FailureDiagnosis,

    /// <summary>The runtime overview assessment focus.</summary>
    RuntimeOverview,

    /// <summary>The historical review assessment focus.</summary>
    HistoricalReview
}

/// <summary>
///     Relative ranges are frozen only when the worker gathers evidence.
/// </summary>
public enum AssessmentRange
{
    /// <summary>Resolves the default time window from deployment status.</summary>
    Automatic,

    /// <summary>Selects the last 15 minutes of recorded evidence.</summary>
    Last15Minutes,

    /// <summary>The last hour evidence range.</summary>
    LastHour,

    /// <summary>Selects the last 24 hours of recorded evidence.</summary>
    Last24Hours,

    /// <summary>The deployment lifetime evidence range.</summary>
    DeploymentLifetime,

    /// <summary>Uses the explicitly supplied UTC start and end bounds.</summary>
    Custom
}

/// <summary>
///     Typed, independently selectable log families, including explicitly unclassified legacy
///     data.
/// </summary>
[Flags]
public enum AssessmentSources
{
    /// <summary>Selects no diagnostic log sources.</summary>
    None = 0,

    /// <summary>The build diagnostic source selection.</summary>
    Build = 1,

    /// <summary>The web diagnostic source selection.</summary>
    Web = 2,

    /// <summary>The database diagnostic source selection.</summary>
    Database = 4,

    /// <summary>Selects GitHub Actions workflow and job observations.</summary>
    GitHub = 8,

    /// <summary>Selects Azure platform and revision observations.</summary>
    Azure = 16,

    /// <summary>The deployment diagnostic source selection.</summary>
    Deployment = 32,

    /// <summary>The other diagnostic source selection.</summary>
    Other = 64,

    /// <summary>Selects every supported diagnostic log source.</summary>
    All = 127
}

/// <summary>
///     Non-secret immutable request options; null container lists mean all, empty lists mean
///     none.
/// </summary>
/// <param name="Kind">The assessment focus requested or resolved for the deployment.</param>
/// <param name="Sources">The selected diagnostic source flags.</param>
/// <param name="IncludeMetrics">
///     Whether recorded numeric observations are selected as analysis evidence.
/// </param>
/// <param name="Range">The selected frozen observation window or assessment range.</param>
/// <param name="Start">The lower bound of the selected observation window.</param>
/// <param name="End">The upper bound of the selected observation window.</param>
/// <param name="LogContainers">Selected log container names; null selects all recorded names.</param>
/// <param name="MetricContainers">
///     Selected metric container names; null selects all recorded names where supported.
/// </param>
public sealed record AssessmentSelection(
    AssessmentKind Kind = AssessmentKind.Automatic,
    AssessmentSources Sources = AssessmentSources.All,
    bool IncludeMetrics = true,
    AssessmentRange Range = AssessmentRange.Automatic,
    DateTimeOffset? Start = null,
    DateTimeOffset? End = null,
    string[]? LogContainers = null,
    string[]? MetricContainers = null)
{
    /// <summary>
    ///     Rejects invalid selection data and canonicalizes arrays for stable request identity.
    /// </summary>
    /// <returns>A validated independent selection with canonical container names and UTC times.</returns>
    /// <exception cref="ArgumentException">
    ///     The focus, sources, custom bounds or container selection is invalid.
    /// </exception>
    public AssessmentSelection Normalize()
    {
        if (!Enum.IsDefined(Kind) || !Enum.IsDefined(Range) || (Sources & ~AssessmentSources.All) != 0 ||
            (Range == AssessmentRange.Custom &&
             (Start is null || End is null || Start < DateTimeOffset.UnixEpoch || End <= Start ||
              End - Start > TimeSpan.FromDays(365))) ||
            (Range != AssessmentRange.Custom && (Start is not null || End is not null)))
            throw new ArgumentException("Invalid assessment selection.");
        return this with
        {
            LogContainers = Names(LogContainers), MetricContainers = Names(MetricContainers),
            Start = Start?.ToUniversalTime(), End = End?.ToUniversalTime()
        };
    }

    /// <summary>
    ///     Canonical persisted representation, without payload snapshots.
    /// </summary>
    /// <returns>Deterministic JSON for the normalized immutable selection.</returns>
    /// <exception cref="ArgumentException">
    ///     The saved or current selection violates the supported bounds.
    /// </exception>
    public string CanonicalJson()
    {
        return JsonSerializer.Serialize(Normalize());
    }

    /// <summary>
    ///     Legacy requests default to all supported evidence.
    /// </summary>
    /// <param name="json">The saved assessment selection JSON; null selects the default options.</param>
    /// <returns>The normalized saved selection, or default options when JSON is absent.</returns>
    /// <exception cref="ArgumentException">
    ///     The saved or current selection violates the supported bounds.
    /// </exception>
    /// <exception cref="System.Text.Json.JsonException">
    ///     The supplied text is not valid selection JSON.
    /// </exception>
    public static AssessmentSelection Read(string? json)
    {
        if (json is null) return new AssessmentSelection();
        if (json.Length > 131072) throw new ArgumentException("Selection exceeds its supported bound.");
        return (JsonSerializer.Deserialize<AssessmentSelection>(json) ??
                throw new ArgumentException("Invalid selection.")).Normalize();
    }

    /// <summary>
    ///     Bounds recorded channel selectors and gives equivalent choices a stable ordering.
    /// </summary>
    /// <param name="values">The values to validate and copy.</param>
    /// <returns>Sorted distinct validated names; null continues to mean all recorded containers.</returns>
    /// <exception cref="ArgumentException">
    ///     Names exceed the count or length bounds, are blank, or contain control characters.
    /// </exception>
    private static string[]? Names(string[]? values)
    {
        if (values is null) return null;
        if (values.Length > 64 ||
            values.Any(v => string.IsNullOrWhiteSpace(v) || v.Length > 128 || v.Any(char.IsControl)))
            throw new ArgumentException("Invalid container selection.");
        return values.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
    }

    /// <summary>
    ///     Uses the observed status, never a guessed success from logs.
    /// </summary>
    /// <param name="status">The recorded deployment runtime state.</param>
    /// <returns>The explicit focus or the focus inferred from the current deployment status.</returns>
    public AssessmentKind Resolve(DeploymentStatus status)
    {
        return Kind != AssessmentKind.Automatic
            ? Kind
            : status switch
            {
                DeploymentStatus.Starting => AssessmentKind.Startup,
                DeploymentStatus.Failed => AssessmentKind.FailureDiagnosis,
                DeploymentStatus.Running => AssessmentKind.RuntimeOverview, _ => AssessmentKind.HistoricalReview
            };
    }

    /// <summary>
    ///     Produces a bounded absolute UTC window and a visible truncation marker.
    /// </summary>
    /// <param name="status">The recorded deployment runtime state.</param>
    /// <param name="created">The deployment creation time used to bound evidence.</param>
    /// <param name="activityEnd">The last recorded deployment activity time.</param>
    /// <param name="now">The current time used for window validation or duration calculation.</param>
    /// <returns>
    ///     The bounded effective evidence interval, including whether its lifetime was truncated.
    /// </returns>
    /// <exception cref="ArgumentException">
    ///     The effective end time is in the future or does not follow the Unix epoch.
    /// </exception>
    public AssessmentWindow Window(DeploymentStatus status, DateTimeOffset created, DateTimeOffset activityEnd,
        DateTimeOffset now)
    {
        var range = Range == AssessmentRange.Automatic
            ? status is DeploymentStatus.Starting or DeploymentStatus.Running
                ? AssessmentRange.LastHour
                : AssessmentRange.DeploymentLifetime
            : Range;
        var end = range == AssessmentRange.Custom ? End!.Value :
            range != AssessmentRange.DeploymentLifetime ||
            status is DeploymentStatus.Starting or DeploymentStatus.Running ? now :
            activityEnd < now ? activityEnd : now;
        var start = range switch
        {
            AssessmentRange.Custom => Start!.Value, AssessmentRange.Last15Minutes => end.AddMinutes(-15),
            AssessmentRange.LastHour => end.AddHours(-1), AssessmentRange.Last24Hours => end.AddHours(-24), _ => created
        };
        if (end > now || end <= DateTimeOffset.UnixEpoch)
            throw new ArgumentException("Range must end in the recorded past.");
        if (start < created) start = created;
        var truncated = end - start > TimeSpan.FromDays(365);
        if (truncated) start = end.AddDays(-365);
        if (start > end) start = end;
        return new AssessmentWindow(start, end, truncated);
    }
}

/// <summary>
///     Actual evidence interval, including explicit lifetime truncation.
/// </summary>
/// <param name="Start">The lower bound of the selected observation window.</param>
/// <param name="End">The upper bound of the selected observation window.</param>
/// <param name="Truncated">Whether bounded selection omitted part of the requested evidence.</param>
public sealed record AssessmentWindow(DateTimeOffset Start, DateTimeOffset End, bool Truncated);

/// <summary>
///     Recorded interpretation inputs; no diagnostic text is persisted here.
/// </summary>
/// <param name="Requested">The immutable assessment options supplied at admission.</param>
/// <param name="EffectiveKind">The assessment focus resolved from the recorded deployment status.</param>
/// <param name="ObservedStatus">The deployment runtime status recorded when evidence was collected.</param>
/// <param name="ObservedOutcome">
///     The deployment preparation outcome recorded when evidence was collected.
/// </param>
/// <param name="CollectedAt">The UTC time at which evidence was collected.</param>
/// <param name="Window">The frozen evidence time window and truncation metadata.</param>
public sealed record AssessmentProvenance(
    AssessmentSelection Requested,
    AssessmentKind EffectiveKind,
    DeploymentStatus ObservedStatus,
    DeploymentOutcome ObservedOutcome,
    DateTimeOffset CollectedAt,
    AssessmentWindow Window);

/// <summary>
///     Shared metadata-only classification; message text is never inspected.
/// </summary>
public static class AssessmentClassification
{
    /// <summary>
    ///     Classifies provider observations from their recorded typed identity only.
    /// </summary>
    /// <param name="e">The normalized deployment observation.</param>
    /// <returns>The typed assessment source inferred from the recorded diagnostic metadata.</returns>
    public static AssessmentSources Source(DeploymentDiagnosticEvent e)
    {
        return e.Source switch
        {
            DeploymentDiagnosticSource.GitHubActions => AssessmentSources.GitHub,
            DeploymentDiagnosticSource.DockerCompose => AssessmentSources.Build,
            DeploymentDiagnosticSource.AzureContainerApps => e.SourceIdentity?.Stream switch
            {
                DeploymentDiagnosticStream.StandardOutput or DeploymentDiagnosticStream.StandardError =>
                    AssessmentSources.Web,
                DeploymentDiagnosticStream.Control or DeploymentDiagnosticStream.System => AssessmentSources.Azure,
                _ => AssessmentSources.Other
            },
            DeploymentDiagnosticSource.AutoMate or DeploymentDiagnosticSource.DockerDaemon => AssessmentSources
                .Deployment,
            DeploymentDiagnosticSource.DockerContainer => e.SourceIdentity?.Component switch
            {
                DeploymentDiagnosticComponent.Web => AssessmentSources.Web,
                DeploymentDiagnosticComponent.Database => AssessmentSources.Database,
                DeploymentDiagnosticComponent.Build => AssessmentSources.Build, _ => AssessmentSources.Other
            },
            _ => AssessmentSources.Other
        };
    }

    /// <summary>
    ///     Applies time, source and individual channel selection before accepting a candidate.
    /// </summary>
    /// <param name="entry">The archived diagnostic envelope to classify or filter.</param>
    /// <param name="selection">The immutable source, container, focus and time-range options.</param>
    /// <param name="window">The frozen evidence time window and truncation metadata.</param>
    /// <returns>Whether the archived event satisfies the selected sources, containers and UTC window.</returns>
    public static bool Matches(DeploymentLogEnvelope entry, AssessmentSelection selection, AssessmentWindow window)
    {
        var e = entry.Event;
        if (e.TimestampUtc < window.Start || e.TimestampUtc > window.End || e.Kind == DeploymentDiagnosticKind.Metric ||
            entry.Channel is null)
            return false;
        var source = Source(e);
        return selection.Sources.HasFlag(source) &&
               (source is not (AssessmentSources.Web or AssessmentSources.Database) ||
                selection.LogContainers is null ||
                selection.LogContainers.Contains(entry.Channel, StringComparer.Ordinal));
    }
}