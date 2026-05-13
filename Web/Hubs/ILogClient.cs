namespace Web.Hubs;


/// <summary>
///    SignalR client interface for receiving real-time logs and metrics from the server.
/// </summary>
public interface ILogClient
{
    /// <summary>
    ///     Receives a build log message from the server and processes it on the client side.
    /// </summary>
    /// <param name="message">The build log message received from the server.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    Task ReceiveBuildLog(string message);

    /// <summary>
    ///    Receives a container log message from the server, associated with a specific container, and processes it on the client side.
    /// </summary>
    /// <param name="containerName">The name of the container associated with the log message.</param>
    /// <param name="message">The container log message received from the server.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    Task ReceiveContainerLog(string containerName, string message);

    /// <summary>
    ///     Receives container metrics from the server and processes them on the client side.
    /// </summary>
    /// <param name="containerName">The name of the container associated with the metrics.</param>
    /// <param name="cpuUsage">The CPU usage metrics received from the server.</param>
    /// <param name="memoryUsage">The memory usage metrics received from the server.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    Task ReceiveContainerMetrics(string containerName, string cpuUsage, string memoryUsage);
}