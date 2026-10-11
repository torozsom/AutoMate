namespace Infrastructure.Azure;

/// <summary>OAuth and polling settings for Azure Monitor Logs runtime collection.</summary>
public sealed class AzureMonitorLogsOptions
{
    /// <summary>Configuration section name.</summary>
    public const string SectionName = "AzureMonitorLogs";

    /// <summary>Microsoft Entra token endpoint.</summary>
    public string TokenEndpoint { get; set; } = string.Empty;

    /// <summary>OAuth application client ID.</summary>
    public string ClientId { get; set; } = string.Empty;

    /// <summary>OAuth application client secret. This is supplied from secure configuration.</summary>
    public string ClientSecret { get; set; } = string.Empty;

    /// <summary>Azure Monitor Logs resource scope.</summary>
    public string Scope { get; set; } = "https://api.loganalytics.io/.default";

    /// <summary>Maximum number of records returned for one source poll.</summary>
    public int BatchSize { get; set; } = 500;

    public int MaximumPagesPerPoll { get; set; } = 32;

    /// <summary>Overlap used to account for delayed log ingestion.</summary>
    public TimeSpan OverlapWindow { get; set; } = TimeSpan.FromMinutes(2);

    /// <summary>Initial lookback when a checkpoint does not yet exist.</summary>
    public TimeSpan InitialLookback { get; set; } = TimeSpan.FromMinutes(10);

    /// <summary>Age after which a delivered record is reported as delayed ingestion.</summary>
    public TimeSpan FreshnessWarningAge { get; set; } = TimeSpan.FromMinutes(5);
}