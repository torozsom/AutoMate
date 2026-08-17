using Core.Enums;

namespace Services.Orchestration;

/// <summary>
///     Publishes deployment status transitions to in-process subscribers.
/// </summary>
public interface IDeploymentStatusNotifier
{
    /// <summary>
    ///     Raised when a deployment changes status.
    /// </summary>
    event Action<Guid, DeploymentStatus> OnStatusChanged;

    /// <summary>
    ///     Notifies subscribers that the status of a deployment has changed.
    /// </summary>
    /// <param name="projectId">The application/project ID whose latest deployment status changed.</param>
    /// <param name="status">The new deployment status.</param>
    void NotifyStatusChanged(Guid projectId, DeploymentStatus status);
}