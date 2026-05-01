namespace Services.LogStreaming;

/// <summary>
///     Represents a service responsible for streaming build logs in real-time.
/// </summary>
public interface ILogStreamer
{
    /// Streams build logs for a specific project. This method is asynchronous and can be used to send log messages to clients in real-time.
    Task StreamBuildLogsAsync(Guid projectId, string message);

    /// Streams container logs for a specific project and container. This method is asynchronous and can be used to send log messages to clients in real-time.
    Task StreamContainerLogsAsync(Guid projectId, string containerName, string message);
}