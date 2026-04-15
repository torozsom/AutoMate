using Core.Enums;

namespace Core.Entities;

/// <summary>
///     Represents a deployment of a project, including the
///     time it was deployed, its status, and any logs associated with the deployment.
/// </summary>
public class Deployment
{
    /// <summary>
    ///     Gets or sets the unique identifier for the deployment.
    /// </summary>
    public Guid Id { get; set; }

    /// <summary>
    ///     Gets or sets the unique identifier of the project associated with this deployment.
    /// </summary>
    public Guid CsProjectId { get; set; }

    /// <summary>
    ///     Gets or sets the timestamp when the deployment was created.
    ///     Defaults to the current UTC time.
    /// </summary>
    public DateTimeOffset DeployedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>
    ///     Gets or sets the current status of the deployment.
    /// </summary>
    public DeploymentStatus Status { get; set; }

    /// <summary>
    ///     Gets or sets any logs or output generated during the deployment process.
    ///     This property can be null if no logs are available.
    /// </summary>
    public string? Logs { get; set; }

    /// <summary>
    ///     Gets or sets a reference to the project associated with this deployment.
    /// </summary>
    public CsProject? CsProject { get; set; }
}