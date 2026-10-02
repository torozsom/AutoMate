namespace Application.Orchestration;

/// <summary>Bounds GitHub Actions monitoring and untrusted log delivery work.</summary>
public sealed class GitHubWorkflowMonitoringOptions
{
    /// <summary>Configuration section for workflow monitoring limits.</summary>
    public const string SectionName = "GitHubWorkflowMonitoring";

    /// <summary>Seconds between workflow status polls.</summary>
    public int PollIntervalSeconds { get; init; } = 10;

    /// <summary>Maximum minutes to wait for a workflow conclusion.</summary>
    public int MaximumMonitoringMinutes { get; init; } = 60;
}