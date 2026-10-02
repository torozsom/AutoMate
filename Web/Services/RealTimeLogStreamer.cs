using Application.Abstractions.Logging;
using Microsoft.AspNetCore.SignalR;
using Web.Hubs;

namespace Web.Services;

/// <summary>
///     Streams deployment logs and container metrics to project-specific SignalR groups.
/// </summary>
/// <param name="hubContext">The SignalR hub context.</param>
public sealed class RealTimeLogStreamer(IHubContext<LogHub, ILogClient> hubContext) : ILogStreamer
{
    /// <inheritdoc />
    public async Task StreamTerminalLogAsync(Guid projectId, string terminalChannel, string message)
    {
        ValidateProjectId(projectId);
        ArgumentException.ThrowIfNullOrWhiteSpace(terminalChannel);

        await hubContext.Clients
            .Group(LogHub.GetProjectGroupName(projectId))
            .ReceiveTerminalLog(terminalChannel, message);
    }

    /// <inheritdoc />
    public async Task StreamContainerMetricsAsync(Guid projectId, string containerName, string cpuUsage,
        string memoryUsage)
    {
        ValidateProjectId(projectId);
        ArgumentException.ThrowIfNullOrWhiteSpace(containerName);

        await hubContext.Clients
            .Group(LogHub.GetProjectGroupName(projectId))
            .ReceiveContainerMetrics(containerName, cpuUsage, memoryUsage);
    }


    /// <summary>
    ///     Validates the project ID to prevent publishing to malformed SignalR group names.
    /// </summary>
    /// <param name="projectId">The project ID to validate.</param>
    /// <exception cref="ArgumentException">Thrown if the project ID is empty.</exception>
    private static void ValidateProjectId(Guid projectId)
    {
        if (projectId == Guid.Empty)
            throw new ArgumentException("Project id must not be empty.", nameof(projectId));
    }
}