using System.Text.Json;
using Application.Abstractions.Diagnostics;
using Domain.Enums;

namespace Application.Ai;

/// <summary>Owner-selected focus; Automatic is resolved from persisted runtime status at collection time.</summary>
public enum AssessmentKind
{
    Automatic,
    Startup,
    FailureDiagnosis,
    RuntimeOverview,
    HistoricalReview
}

/// <summary>Relative ranges are frozen only when the worker gathers evidence.</summary>
public enum AssessmentRange
{
    Automatic,
    Last15Minutes,
    LastHour,
    Last24Hours,
    DeploymentLifetime,
    Custom
}

/// <summary>Typed, independently selectable log families, including explicitly unclassified legacy data.</summary>
[Flags]
public enum AssessmentSources
{
    None = 0,
    Build = 1,
    Web = 2,
    Database = 4,
    GitHub = 8,
    Azure = 16,
    Deployment = 32,
    Other = 64,
    All = 127
}

/// <summary>Non-secret immutable request options; null container lists mean all, empty lists mean none.</summary>
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
    /// <summary>Rejects invalid selection data and canonicalizes arrays for stable request identity.</summary>
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

    /// <summary>Canonical persisted representation, without payload snapshots.</summary>
    public string CanonicalJson()
    {
        return JsonSerializer.Serialize(Normalize());
    }

    /// <summary>Legacy requests default to all supported evidence.</summary>
    public static AssessmentSelection Read(string? json)
    {
        if (json is null) return new AssessmentSelection();
        if (json.Length > 131072) throw new ArgumentException("Selection exceeds its supported bound.");
        return (JsonSerializer.Deserialize<AssessmentSelection>(json) ??
                throw new ArgumentException("Invalid selection.")).Normalize();
    }

    /// <summary>Bounds recorded channel selectors and gives equivalent choices a stable ordering.</summary>
    private static string[]? Names(string[]? values)
    {
        if (values is null) return null;
        if (values.Length > 64 ||
            values.Any(v => string.IsNullOrWhiteSpace(v) || v.Length > 128 || v.Any(char.IsControl)))
            throw new ArgumentException("Invalid container selection.");
        return values.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
    }

    /// <summary>Uses the observed status, never a guessed success from logs.</summary>
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

    /// <summary>Produces a bounded absolute UTC window and a visible truncation marker.</summary>
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

/// <summary>Actual evidence interval, including explicit lifetime truncation.</summary>
public sealed record AssessmentWindow(DateTimeOffset Start, DateTimeOffset End, bool Truncated);

/// <summary>Recorded interpretation inputs; no diagnostic text is persisted here.</summary>
public sealed record AssessmentProvenance(
    AssessmentSelection Requested,
    AssessmentKind EffectiveKind,
    DeploymentStatus ObservedStatus,
    DeploymentOutcome ObservedOutcome,
    DateTimeOffset CollectedAt,
    AssessmentWindow Window);

/// <summary>Shared metadata-only classification; message text is never inspected.</summary>
public static class AssessmentClassification
{
    /// <summary>Classifies provider observations from their recorded typed identity only.</summary>
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

    /// <summary>Applies time, source and individual channel selection before accepting a candidate.</summary>
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