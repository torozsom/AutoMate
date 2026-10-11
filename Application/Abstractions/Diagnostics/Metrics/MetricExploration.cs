namespace Application.Abstractions.Diagnostics;

/// <summary>Absolute half-open UTC window shared by chart and statistics requests.</summary>
public sealed record MetricTimeRange(DateTimeOffset Start, DateTimeOffset End)
{
    /// <summary>Expected aggregation interval used to render honest gaps between observations.</summary>
    public TimeSpan Interval => End - Start > TimeSpan.FromDays(1673) ? TimeSpan.FromDays(32)
        : End - Start > TimeSpan.FromDays(239) ? TimeSpan.FromDays(7)
        : End - Start > TimeSpan.FromDays(1) ? TimeSpan.FromDays(1)
        : TimeSpan.FromSeconds(Math.Max(60, Math.Ceiling((End - Start).TotalSeconds / 239 / 60) * 60));

    /// <summary>Human-readable aggregation metadata; imported intervals retain a separate denominator.</summary>
    public string Aggregation => End - Start > TimeSpan.FromDays(1673) ? "UTC calendar months"
        : End - Start > TimeSpan.FromDays(239) ? "UTC weeks (Monday)"
        : End - Start > TimeSpan.FromDays(1) ? "UTC days" : "UTC minute intervals";

    /// <summary>Checks chart bounds independently of the finite AI context policy.</summary>
    public void Validate(DateTimeOffset now)
    {
        if (Start.Offset != TimeSpan.Zero || End.Offset != TimeSpan.Zero || End - Start < TimeSpan.FromMinutes(5) ||
            Start < End.AddYears(-5) || End > now)
            throw new ArgumentException(
                "Choose a UTC range between five minutes and five calendar years, ending no later than now.");
    }

    /// <summary>Freezes a relative selection at the supplied collection clock.</summary>
    public static MetricTimeRange Preset(string choice, DateTimeOffset now)
    {
        return new MetricTimeRange(choice switch
        {
            "10m" => now.AddMinutes(-10), "30m" => now.AddMinutes(-30),
            "1h" => now.AddHours(-1), "6h" => now.AddHours(-6), "24h" => now.AddDays(-1),
            "7d" => now.AddDays(-7), "30d" => now.AddDays(-30), "90d" => now.AddDays(-90),
            "1y" => now.AddYears(-1), "5y" => now.AddYears(-5),
            _ => throw new ArgumentException("Unknown metric range.")
        }, now);
    }

    /// <summary>UTC bucket beginning with no more than 240 buckets across a five-year window.</summary>
    public DateTimeOffset Bucket(DateTimeOffset time)
    {
        var length = End - Start;
        if (length > TimeSpan.FromDays(1673))
            return new DateTimeOffset(time.Year, time.Month, 1, 0, 0, 0, TimeSpan.Zero);
        if (length > TimeSpan.FromDays(239))
            return new DateTimeOffset(time.UtcDateTime.Date.AddDays(-((int)time.DayOfWeek + 6) % 7), TimeSpan.Zero);
        if (length > TimeSpan.FromDays(1))
            return new DateTimeOffset(time.UtcDateTime.Date, TimeSpan.Zero);
        var seconds = Math.Max(60, Math.Ceiling(length.TotalSeconds / 239 / 60) * 60);
        return Start.AddSeconds(Math.Floor((time - Start).TotalSeconds / seconds) * seconds);
    }
}

/// <summary>Owner-authorized scope; a deployment must belong to the optional selected project.</summary>
public sealed record MetricExplorationQuery(
    Guid Owner,
    MetricTimeRange Range,
    Guid? Project = null,
    Guid? Deployment = null,
    int Page = 1,
    int PageSize = 25,
    string? Container = null);

/// <summary>Recorded metric sufficient statistics, preserving deployment and container identity.</summary>
public sealed record MetricObservation(
    Guid Project,
    Guid Deployment,
    string Container,
    string Metric,
    string Unit,
    DateTimeOffset Timestamp,
    long Samples,
    double Sum,
    double Minimum,
    double Maximum,
    bool Incomplete = false,
    long ImportedIntervals = 0)
{
    /// <summary>Uses observed samples, or explicitly identified imported interval averages.</summary>
    public double Average => Sum / Math.Max(1, Samples > 0 ? Samples : ImportedIntervals);
}

/// <summary>Bounded chart observations and a separate paginated numeric alternative.</summary>
public sealed record MetricExplorationResult(
    IReadOnlyList<MetricObservation> Chart,
    IReadOnlyList<MetricObservation> Items,
    int Total,
    int Page,
    int PageSize,
    MetricTimeRange Range,
    string? Availability = null);

/// <summary>Reads persisted observations without consulting Azure or deployment providers.</summary>
public interface IMetricExploration
{
    /// <summary>Authorizes scope, aggregates selected evidence, and pages statistics independently of charts.</summary>
    Task<MetricExplorationResult> ReadAsync(MetricExplorationQuery query, CancellationToken token = default);
}

/// <summary>Private batch range request; the host resolves and authorizes every deployment.</summary>
public sealed record ArchiveMetricBatchRequest(
    Guid Owner,
    MetricTimeRange Range,
    Guid? Project = null,
    Guid? Deployment = null,
    int Offset = 0,
    string? Container = null);

/// <summary>At most ten deployment partitions per private request; continuation is metadata only.</summary>
public sealed record ArchiveMetricBatch(
    IReadOnlyList<MetricObservation> Items,
    int? NextOffset,
    string? Availability = null);