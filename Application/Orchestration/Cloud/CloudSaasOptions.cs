namespace Application.Orchestration;

/// <summary>Cluster-wide SaaS cloud admission limits.</summary>
public sealed class CloudSaasOptions
{
    /// <summary>Configuration section name.</summary>
    public const string SectionName = "CloudSaas";

    /// <summary>Maximum waiting requests per account.</summary>
    public int MaxQueuedPerUser { get; set; } = 10;

    /// <summary>Maximum launch operations per account.</summary>
    public int MaxActivePerUser { get; set; } = 2;

    /// <summary>Maximum launch operations per GitHub App installation.</summary>
    public int MaxActivePerInstallation { get; set; } = 4;

    /// <summary>Maximum launch operations for the entire cluster.</summary>
    public int MaxActiveGlobally { get; set; } = 32;

    /// <summary>Maximum launch operations a single process starts concurrently.</summary>
    public int MaxActivePerWorker { get; set; } = 8;

    /// <summary>Seconds between admission scans when no eligible run was found.</summary>
    public int SchedulerPollSeconds { get; set; } = 2;
}