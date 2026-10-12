using Domain.Enums;
using Microsoft.Extensions.Logging;

namespace Application.Orchestration;

/// <summary>
///     In-process publisher for deployment status changes consumed by Blazor UI components.
/// </summary>
/// <param name="logger">The logger used for fixed operational events without diagnostic payloads.</param>
public sealed class DeploymentStatusNotifier(ILogger<DeploymentStatusNotifier> logger) : IDeploymentStatusNotifier
{
    /// <summary>Publishes project runtime transitions while isolating subscriber failures.</summary>
    public event Action<Guid, DeploymentStatus>? OnStatusChanged;

    /// <summary>
    ///     Publishes a deployment transition while isolating and diagnosing subscriber failures.
    /// </summary>
    /// <param name="projectId">The project identity used for authorization or correlation.</param>
    /// <param name="status">The recorded deployment runtime state.</param>
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
                logger.LogWarning(
                    "Deployment status subscriber failed for project {ProjectId} with status {Status}: {FailureType}.",
                    projectId, status, ex.GetType().Name);
            }
    }
}