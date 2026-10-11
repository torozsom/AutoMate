namespace Application.Abstractions.Diagnostics;

/// <summary>Short-lived, authorized viewers requesting runtime collection and durable replay while viewing.</summary>
public interface IDeploymentRuntimeViewers
{
    /// <summary>Renews an authorized connection's interest in one deployment.</summary>
    void Renew(string connectionId, Guid projectId, Guid deploymentId);

    /// <summary>Removes a connection, optionally only for one project.</summary>
    void Remove(string connectionId, Guid? projectId = null);

    /// <summary>Checks whether a deployment has an unexpired viewer.</summary>
    bool HasViewers(Guid projectId, Guid deploymentId);
}