using Application.Abstractions.Diagnostics;

namespace Application.Abstractions.Logging;

/// <summary>
///     Represents a service responsible for streaming source-aware terminal data in real-time.
/// </summary>
public interface ILogStreamer
{
    /// <summary>Streams redacted output to one stable, source-aware terminal channel.</summary>
    Task StreamTerminalLogAsync(DeploymentTerminalLog terminalLog, CancellationToken cancellationToken = default);

    /// <summary>Streams a safe availability notice; cancellation must terminate a pending transport write.</summary>
    Task StreamTerminalNoticeAsync(Guid projectId, string message, CancellationToken cancellationToken = default);

    /// <summary>Streams container metrics for a specific project and container.</summary>
    Task StreamContainerMetricsAsync(Guid projectId, string containerName, string cpuUsage, string memoryUsage,
        CancellationToken cancellationToken = default);
}