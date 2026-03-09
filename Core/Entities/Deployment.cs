using Core.Enums;

namespace Core.Entities;

/// <summary>
///     Represents a deployment of a project, including the
///     time it was deployed, its status, and any logs associated with the deployment.
/// </summary>
public class Deployment
{
    /// The unique identifier for the deployment.
    public Guid Id { get; set; }

    /// The unique identifier of the project associated with this deployment.
    public Guid ProjectId { get; set; }

    /// The timestamp when the deployment was created, defaulting to the current UTC time.
    public DateTimeOffset DeployedAt { get; set; } = DateTimeOffset.UtcNow;

    /// The current status of the deployment, represented by the DeploymentStatus enum.
    public DeploymentStatus Status { get; set; }

    /// Any logs or output generated during the deployment process, which can be null if no logs are available.
    public string? Logs { get; set; }

    /// A reference to the associated project, which can be null if the project is not loaded or has been deleted.
    public Project? Project { get; set; }
}