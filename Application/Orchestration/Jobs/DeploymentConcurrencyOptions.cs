namespace Application.Orchestration.Jobs;

/// <summary>
///     Per-process limits for the in-memory deployment scheduler.
/// </summary>
public sealed class DeploymentConcurrencyOptions
{
    /// <summary>Configuration section containing per-process scheduler limits.</summary>
    public const string SectionName = "DeploymentConcurrency";

    /// <summary>0 leaves builds uncapped by default; -1 opts into CPU-aware capacity.</summary>
    public int MaxLocalBuilds { get; set; }

    /// <summary>0 leaves independent local stops uncapped; positive values opt into a per-process cap.</summary>
    public int MaxLocalStops { get; set; }

    /// <summary>Maximum concurrent cloud deployments, validated from 1 through 16.</summary>
    public int MaxCloudDeployments { get; set; } = 4;

    /// <summary>Maximum admitted waiting jobs, validated from 1 through 1,000.</summary>
    public int MaxQueuedJobs { get; set; } = 100;
}
