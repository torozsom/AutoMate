namespace Application.Orchestration;

/// <summary>Per-process limits for the in-memory deployment scheduler.</summary>
public sealed class DeploymentConcurrencyOptions
{
    public const string SectionName = "DeploymentConcurrency";

    /// <summary>-1 chooses a CPU-aware default; 0 removes AutoMate's local build cap.</summary>
    public int MaxLocalBuilds { get; set; } = -1;

    public int MaxCloudDeployments { get; set; } = 4;
    public int MaxQueuedJobs { get; set; } = 100;
}