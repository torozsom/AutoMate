using Domain.Enums.Deployments;

namespace Application.Orchestration.Status;

/// <summary>
///     Publishes deployment status transitions to in-process subscribers.
/// </summary>
public interface IDeploymentStatusNotifier
{
    /// <summary>Notifies in-process subscribers of a project runtime transition.</summary>
    event Action<Guid, DeploymentStatus> OnStatusChanged;

    /// <summary>
    ///     Notifies subscribers that the status of a deployment has changed.
    /// </summary>
    /// <param name="projectId">The application/project ID whose latest deployment status changed.</param>
    /// <param name="status">The new deployment status.</param>
    void NotifyStatusChanged(Guid projectId, DeploymentStatus status);
}
