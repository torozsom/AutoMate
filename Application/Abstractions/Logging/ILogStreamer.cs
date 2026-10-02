using Application.Abstractions.Diagnostics;

namespace Application.Abstractions.Logging;

/// <summary>
///     Represents a service responsible for streaming source-aware terminal data in real-time.
/// </summary>
public interface ILogStreamer
{
    /// <summary>Streams redacted output to one stable, source-aware terminal channel.</summary>
    Task StreamTerminalLogAsync(DeploymentTerminalLog terminalLog);

    Task StreamTerminalNoticeAsync(Guid projectId, string message);

    /// <summary>Streams container metrics for a specific project and container.</summary>
    Task StreamContainerMetricsAsync(Guid projectId, string containerName, string cpuUsage, string memoryUsage);
}