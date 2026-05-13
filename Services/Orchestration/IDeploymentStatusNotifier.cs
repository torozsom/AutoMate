using Core.Enums;

namespace Services.Orchestration;

public interface IDeploymentStatusNotifier
{
    /// Event that is raised when the status of a deployment changes.
    event Action<Guid, DeploymentStatus> OnStatusChanged;

    /// Notifies subscribers that the status of a deployment has changed.
    void NotifyStatusChanged(Guid projectId, DeploymentStatus status);
}