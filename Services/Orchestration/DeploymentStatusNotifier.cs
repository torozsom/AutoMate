using Core.Enums;

namespace Services.Orchestration;

public class DeploymentStatusNotifier : IDeploymentStatusNotifier
{
    /// <inheritdoc />
    public event Action<Guid, DeploymentStatus>? OnStatusChanged;

    /// <inheritdoc />
    public void NotifyStatusChanged(Guid projectId, DeploymentStatus status)
    {
        OnStatusChanged?.Invoke(projectId, status);
    }
}