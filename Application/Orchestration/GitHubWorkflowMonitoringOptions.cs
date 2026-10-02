namespace Application.Orchestration;

/// <summary>Bounds GitHub Actions monitoring and untrusted log delivery work.</summary>
public sealed class GitHubWorkflowMonitoringOptions
{
    public const string SectionName = "GitHubWorkflowMonitoring";
    public int PollIntervalSeconds { get; init; } = 10;
    public int MaximumMonitoringMinutes { get; init; } = 60;
}