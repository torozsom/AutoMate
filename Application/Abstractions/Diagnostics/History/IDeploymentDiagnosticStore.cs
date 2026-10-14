using Application.Abstractions.Diagnostics.Events;

namespace Application.Abstractions.Diagnostics.History;

/// <summary>
///     Reads and expires redacted legacy diagnostics and builds bounded compatibility context.
/// </summary>
public interface IDeploymentDiagnosticStore
{
    /// <summary>
    ///     Retains the legacy persistence contract; current PostgreSQL adapters reject new payloads.
    /// </summary>
    /// <param name="diagnosticEvent">The normalized diagnostic observation to process.</param>
    /// <param name="terminalChannel">The stable terminal routing channel.</param>
    /// <param name="cancellationToken">Cancels the operation when supported by the underlying work.</param>
    /// <returns>The persisted ordering identifier if an adapter supports legacy writes.</returns>
    /// <exception cref="InvalidOperationException">
    ///     Current storage policy requires the durable telemetry gateway instead of PostgreSQL payload writes.
    /// </exception>
    /// <exception cref="OperationCanceledException">The supplied operation is canceled.</exception>
    Task<long> PersistAsync(DeploymentDiagnosticEvent diagnosticEvent, string? terminalChannel,
        CancellationToken cancellationToken = default);

    /// <summary>
    ///     Reads a bounded recent terminal-history page for one deployment.
    /// </summary>
    /// <param name="projectId">The project identity used for authorization or correlation.</param>
    /// <param name="deploymentId">The exact deployment identity used for ownership or correlation.</param>
    /// <param name="limit">The maximum number of returned or processed records.</param>
    /// <param name="cancellationToken">Cancels the operation when supported by the underlying work.</param>
    /// <returns>
    ///     A task returning recent terminal records with explicit omissions and availability
    ///     metadata.
    /// </returns>
    Task<DeploymentTerminalHistory> ReadRecentAsync(Guid projectId, Guid deploymentId, int limit,
        CancellationToken cancellationToken = default);

    /// <summary>
    ///     Reads a bounded ordered page after a previously delivered terminal event.
    /// </summary>
    /// <param name="projectId">The project identity used for authorization or correlation.</param>
    /// <param name="deploymentId">The exact deployment identity used for ownership or correlation.</param>
    /// <param name="afterOrderId">The last confirmed ordering position; return only later records.</param>
    /// <param name="limit">The maximum number of returned or processed records.</param>
    /// <param name="cancellationToken">Cancels the operation when supported by the underlying work.</param>
    /// <returns>
    ///     A task returning ordered terminal records following the confirmed cursor, with coverage
    ///     metadata.
    /// </returns>
    Task<DeploymentTerminalHistory> ReadAfterAsync(Guid projectId, Guid deploymentId, long afterOrderId,
        int limit, CancellationToken cancellationToken = default);

    /// <summary>
    ///     Deletes a bounded batch of expired diagnostic records.
    /// </summary>
    /// <param name="limit">The maximum number of returned or processed records.</param>
    /// <param name="cancellationToken">Cancels the operation when supported by the underlying work.</param>
    /// <returns>A task returning the number of expired records deleted in this batch.</returns>
    Task<int> DeleteExpiredAsync(int limit, CancellationToken cancellationToken = default);

    /// <summary>
    ///     Builds bounded redacted compatibility context for one deployment.
    /// </summary>
    /// <param name="deploymentId">The exact deployment identity used for ownership or correlation.</param>
    /// <param name="maximumCharacters">The maximum permitted text length.</param>
    /// <param name="cancellationToken">Cancels the operation when supported by the underlying work.</param>
    /// <returns>
    ///     A task returning bounded redacted diagnostic text suitable for legacy context callers.
    /// </returns>
    Task<string> BuildContextAsync(Guid deploymentId, int maximumCharacters,
        CancellationToken cancellationToken = default);
}
