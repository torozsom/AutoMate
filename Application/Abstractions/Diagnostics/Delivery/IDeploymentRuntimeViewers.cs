namespace Application.Abstractions.Diagnostics.Delivery;

/// <summary>
///     Tracks expiring authorized viewing interest; viewer presence does not authorize
///     diagnostic storage.
/// </summary>
public interface IDeploymentRuntimeViewers
{
    /// <summary>
    ///     Renews an authorized connection's interest in one deployment.
    /// </summary>
    /// <param name="connectionId">The authorized connection whose expiring viewing lease is tracked.</param>
    /// <param name="projectId">The project identity used for authorization or correlation.</param>
    /// <param name="deploymentId">The exact deployment identity used for ownership or correlation.</param>
    void Renew(string connectionId, Guid projectId, Guid deploymentId);

    /// <summary>
    ///     Removes a connection, optionally only for one project.
    /// </summary>
    /// <param name="connectionId">The authorized connection whose expiring viewing lease is tracked.</param>
    /// <param name="projectId">The project identity used for authorization or correlation.</param>
    void Remove(string connectionId, Guid? projectId = null);

    /// <summary>
    ///     Checks whether a deployment has an unexpired viewer.
    /// </summary>
    /// <param name="projectId">The project identity used for authorization or correlation.</param>
    /// <param name="deploymentId">The exact deployment identity used for ownership or correlation.</param>
    /// <returns>Whether the deployment has at least one unexpired authorized viewing lease.</returns>
    bool HasViewers(Guid projectId, Guid deploymentId);
}
