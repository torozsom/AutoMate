using Application.Abstractions.Diagnostics;

namespace Web.Hubs;

/// <summary>
///     SignalR client interface for receiving real-time logs and metrics from the server.
/// </summary>
public interface ILogClient
{
    /// <summary>
    ///     Receives native numeric samples with source, timestamp and deployment correlation.
    /// </summary>
    /// <param name="snapshot">Typed resource metrics with project, deployment and provider timestamp.</param>
    /// <returns>A task representing completion of this operation, including any awaited callbacks.</returns>
    Task ReceiveDeploymentMetrics(DeploymentRuntimeMetricSnapshot snapshot);

    /// <summary>
    ///     Receives redacted output for one source-aware terminal channel.
    /// </summary>
    /// <param name="terminalLog">Redacted deployment output with channel and replay identity.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    Task ReceiveTerminalLog(DeploymentTerminalLog terminalLog);

    /// <summary>
    ///     Receives safe availability guidance for display outside deployment terminal output.
    /// </summary>
    /// <param name="message">Display message; secret-bearing provider text must not be supplied.</param>
    /// <returns>A task representing completion of this operation, including any awaited callbacks.</returns>
    Task ReceiveTerminalNotice(string message);

    /// <summary>
    ///     Receives container metrics from the server and processes them on the client side.
    /// </summary>
    /// <param name="containerName">The name of the container associated with the metrics.</param>
    /// <param name="cpuUsage">The CPU usage metrics received from the server.</param>
    /// <param name="memoryUsage">The memory usage metrics received from the server.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    Task ReceiveContainerMetrics(string containerName, string cpuUsage, string memoryUsage);
}