namespace Application.Orchestration.Cloud;

/// <summary>
///     Cluster-wide SaaS cloud admission limits.
/// </summary>
public sealed class CloudSaasOptions
{
    /// <summary>Configuration section name.</summary>
    public const string SectionName = "CloudSaas";

    /// <summary>Maximum waiting requests per account.</summary>
    public int MaxQueuedPerUser { get; init; } = 10;

    /// <summary>Maximum launch operations per account.</summary>
    public int MaxActivePerUser { get; init; } = 2;

    /// <summary>Maximum launch operations per GitHub App installation.</summary>
    public int MaxActivePerInstallation { get; init; } = 4;

    /// <summary>Maximum launch operations for the entire cluster.</summary>
    public int MaxActiveGlobally { get; init; } = 32;

    /// <summary>Maximum launch operations a single process starts concurrently.</summary>
    public int MaxActivePerWorker { get; init; } = 8;

    /// <summary>Seconds between admission scans when no eligible run was found.</summary>
    public int SchedulerPollSeconds { get; init; } = 2;
}
