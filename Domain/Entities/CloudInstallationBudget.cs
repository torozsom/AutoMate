namespace Domain.Entities;

/// <summary>Shared GitHub installation cooldown after provider throttling.</summary>
public sealed class CloudInstallationBudget
{
    /// <summary>Installation whose API usage is paused.</summary>
    public long InstallationId { get; set; }
    /// <summary>Earliest time workers may launch another request for this installation.</summary>
    public DateTimeOffset PausedUntil { get; set; }
    /// <summary>Time of the latest throttle observation.</summary>
    public DateTimeOffset UpdatedAt { get; set; }
    /// <summary>Consecutive provider throttles used for bounded backoff.</summary>
    public int ThrottleCount { get; set; }
}
