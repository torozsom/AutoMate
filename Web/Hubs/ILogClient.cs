namespace Web.Hubs;

/// <summary>
///     SignalR client interface for receiving real-time logs and metrics from the server.
/// </summary>
public interface ILogClient
{
    /// <summary>
    ///     Receives redacted output for one source-aware terminal channel.
    /// </summary>
    /// <param name="terminalChannel">The stable channel used to route output to the correct terminal.</param>
    /// <param name="message">The terminal message received from the server.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    Task ReceiveTerminalLog(string terminalChannel, string message);

    /// <summary>
    ///     Receives container metrics from the server and processes them on the client side.
    /// </summary>
    /// <param name="containerName">The name of the container associated with the metrics.</param>
    /// <param name="cpuUsage">The CPU usage metrics received from the server.</param>
    /// <param name="memoryUsage">The memory usage metrics received from the server.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    Task ReceiveContainerMetrics(string containerName, string cpuUsage, string memoryUsage);
}