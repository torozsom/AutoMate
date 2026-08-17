using Core.Enums;
using Microsoft.Extensions.Logging;

namespace Services.Orchestration;

/// <summary>
///     In-process publisher for deployment status changes consumed by Blazor UI components.
/// </summary>
public sealed class DeploymentStatusNotifier(ILogger<DeploymentStatusNotifier> logger) : IDeploymentStatusNotifier
{
    /// <inheritdoc />
    public event Action<Guid, DeploymentStatus>? OnStatusChanged;

    /// <inheritdoc />
    public void NotifyStatusChanged(Guid projectId, DeploymentStatus status)
    {
        var handlers = OnStatusChanged;
        if (handlers is null) return;

        foreach (Action<Guid, DeploymentStatus> handler in handlers.GetInvocationList())
            try
            {
                // Isolate UI subscribers so a disposed Blazor circuit cannot fail deployment processing.
                handler(projectId, status);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex,
                    "Deployment status subscriber failed for project {ProjectId} with status {Status}.",
                    projectId, status);
            }
    }
}