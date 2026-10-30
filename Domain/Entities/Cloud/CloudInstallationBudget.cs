namespace Domain.Entities.Cloud;

/// <summary>
///     Shared GitHub installation cooldown after provider throttling.
/// </summary>
public sealed class CloudInstallationBudget
{
    /// <summary>Installation whose API usage is paused.</summary>
    public long InstallationId { get; init; }

    /// <summary>Earliest time workers may launch another request for this installation.</summary>
    public DateTimeOffset PausedUntil { get; init; }

    /// <summary>Time of the latest throttle observation.</summary>
    public DateTimeOffset UpdatedAt { get; init; }

    /// <summary>Consecutive provider throttles used for bounded backoff.</summary>
    public int ThrottleCount { get; init; }
}
