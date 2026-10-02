namespace Application.Abstractions.Azure;

/// <summary>
///     Obtains a short-lived Azure Monitor Logs data-plane token for a connected AutoMate user.
/// </summary>
public interface IAzureMonitorLogsTokenProvider
{
    /// <summary>
    ///     Gets a memory-only token suitable for Azure Monitor Logs queries.
    /// </summary>
    Task<AzureMonitorLogsTokenResult> GetTokenAsync(Guid userId, CancellationToken cancellationToken = default);
}

/// <summary>
///     Result of a scoped Azure Monitor Logs token acquisition attempt.
/// </summary>
/// <param name="AccessToken">The token, when acquisition succeeded. It must never be persisted or logged.</param>
/// <param name="FailureReason">A safe, user-facing reason when acquisition failed.</param>
public sealed record AzureMonitorLogsTokenResult(string? AccessToken, string? FailureReason)
{
    /// <summary>Whether an access token is available for a Logs API request.</summary>
    public bool IsSuccess => !string.IsNullOrWhiteSpace(AccessToken);
}