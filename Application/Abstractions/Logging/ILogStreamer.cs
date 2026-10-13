using Application.Abstractions.Diagnostics;

namespace Application.Abstractions.Logging;

/// <summary>
///     Represents a service responsible for streaming source-aware terminal data in real-time.
/// </summary>
public interface ILogStreamer
{
    /// <summary>
    ///     Streams typed deployment metrics; older adapters retain their legacy metric callback.
    /// </summary>
    /// <param name="snapshot">The typed resource observation with project and deployment identity.</param>
    /// <param name="cancellationToken">Cancels the operation when supported by the underlying work.</param>
    /// <returns>
    ///     A task that completes after typed metric delivery; unsupported legacy transports may do
    ///     nothing.
    /// </returns>
    Task StreamDeploymentMetricsAsync(DeploymentRuntimeMetricSnapshot snapshot,
        CancellationToken cancellationToken = default)
    {
        return Task.CompletedTask;
    }

    /// <summary>
    ///     Streams redacted output to one stable, source-aware terminal channel.
    /// </summary>
    /// <param name="terminalLog">The redacted terminal projection with replay identity.</param>
    /// <param name="cancellationToken">Cancels the operation when supported by the underlying work.</param>
    /// <returns>A task that completes after the terminal event delivery attempt.</returns>
    Task StreamTerminalLogAsync(DeploymentTerminalLog terminalLog, CancellationToken cancellationToken = default);

    /// <summary>
    ///     Streams a safe availability notice; cancellation must terminate a pending transport
    ///     write.
    /// </summary>
    /// <param name="projectId">The project identity used for authorization or correlation.</param>
    /// <param name="message">The message supplied to the operation.</param>
    /// <param name="cancellationToken">Cancels the operation when supported by the underlying work.</param>
    /// <returns>A task that completes after the operational notice delivery attempt.</returns>
    Task StreamTerminalNoticeAsync(Guid projectId, string message, CancellationToken cancellationToken = default);

    /// <summary>
    ///     Streams container metrics for a specific project and container.
    /// </summary>
    /// <param name="projectId">The project identity used for authorization or correlation.</param>
    /// <param name="containerName">The exact Docker container name.</param>
    /// <param name="cpuUsage">The existing CPU display value used by the compatibility callback.</param>
    /// <param name="memoryUsage">The existing memory display value used by the compatibility callback.</param>
    /// <param name="cancellationToken">Cancels the operation when supported by the underlying work.</param>
    /// <returns>
    ///     A task representing the container-metric subscription until cancellation or termination.
    /// </returns>
    Task StreamContainerMetricsAsync(Guid projectId, string containerName, string cpuUsage, string memoryUsage,
        CancellationToken cancellationToken = default);
}
