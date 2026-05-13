using Microsoft.AspNetCore.SignalR;
using Services.LogStreaming;
using Web.Hubs;

namespace Web.Services;

/// <summary>
///     A service that implements the ILogStreamer interface to stream build logs in real-time to
///     connected clients using SignalR. This service sends log messages to a specific group associated
///     with the project ID, allowing clients that have joined the group to receive the log updates as they occur.
/// </summary>
/// <param name="hubContext">The SignalR hub context.</param>
public class RealTimeLogStreamer(IHubContext<LogHub, ILogClient> hubContext) : ILogStreamer
{
    /// <summary>
    ///     Streams build logs for a specific project to connected clients in real-time.
    /// </summary>
    /// <param name="projectId">The unique identifier of the project for which logs are being streamed.</param>
    /// <param name="message">The log message to stream to clients.</param>
    /// <returns>A task representing the asynchronous operation of streaming the log message.</returns>
    public async Task StreamBuildLogsAsync(Guid projectId, string message)
    {
        await hubContext.Clients
            .Group($"project-{projectId}")
            .ReceiveBuildLog(message);
    }


    /// <summary>
    ///     Streams container logs for a specific project and container to connected clients in real-time.
    /// </summary>
    /// <param name="projectId">The unique identifier of the project for which logs are being streamed.</param>
    /// <param name="containerName">The name of the container for which logs are being streamed.</param>
    /// <param name="message">The log message to stream to clients.</param>
    /// <returns>A task representing the asynchronous operation of streaming the log message.</returns>
    public async Task StreamContainerLogsAsync(Guid projectId, string containerName, string message)
    {
        await hubContext.Clients
            .Group($"project-{projectId}")
            .ReceiveContainerLog(containerName, message);
    }


    /// <summary>
    ///     Streams container metrics for a specific project and container to connected clients in real-time.
    /// </summary>
    public async Task StreamContainerMetricsAsync(Guid projectId, string containerName, string cpuUsage, string memoryUsage)
    {
        await hubContext.Clients
            .Group($"project-{projectId}")
            .ReceiveContainerMetrics(containerName, cpuUsage, memoryUsage);
    }
}