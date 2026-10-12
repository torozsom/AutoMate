namespace Application.Orchestration;

/// <summary>
///     Per-process limits for the in-memory deployment scheduler.
/// </summary>
public sealed class DeploymentConcurrencyOptions
{
    /// <summary>Configuration section containing per-process scheduler limits.</summary>
    public const string SectionName = "DeploymentConcurrency";

    /// <summary>-1 chooses a CPU-aware default; 0 removes AutoMate's local build cap.</summary>
    public int MaxLocalBuilds { get; set; } = -1;

    /// <summary>Maximum concurrent cloud deployments, validated from 1 through 16.</summary>
    public int MaxCloudDeployments { get; set; } = 4;

    /// <summary>Maximum admitted waiting jobs, validated from 1 through 1,000.</summary>
    public int MaxQueuedJobs { get; set; } = 100;
}