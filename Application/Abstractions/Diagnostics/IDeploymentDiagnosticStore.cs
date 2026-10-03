namespace Application.Abstractions.Diagnostics;

/// <summary>Persists redacted deployment diagnostics and builds bounded, safe diagnostic context.</summary>
public interface IDeploymentDiagnosticStore
{
    Task<long> PersistAsync(DeploymentDiagnosticEvent diagnosticEvent, string? terminalChannel,
        CancellationToken cancellationToken = default);

    Task<DeploymentTerminalHistory> ReadRecentAsync(Guid projectId, Guid deploymentId, int limit,
        CancellationToken cancellationToken = default);

    /// <summary>Reads a bounded ordered page after a previously delivered terminal event.</summary>
    Task<DeploymentTerminalHistory> ReadAfterAsync(Guid projectId, Guid deploymentId, long afterOrderId,
        int limit, CancellationToken cancellationToken = default);

    Task<int> DeleteExpiredAsync(int limit, CancellationToken cancellationToken = default);

    Task<string> BuildContextAsync(Guid deploymentId, int maximumCharacters,
        CancellationToken cancellationToken = default);
}
