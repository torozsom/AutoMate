using Core.Enums;

namespace Core.Entities;


/// <summary>
/// Represents a deployment of a project, including the
/// time it was deployed, its status, and any logs associated with the deployment.
/// </summary>
public class Deployment
{
    public Guid Id { get; set; }

    public Guid ProjectId { get; set; }

    public required DateTimeOffset DeployedAt { get; set; }

    public DeploymentStatus Status { get; set; }

    public string? Logs { get; set; }

    public Project? Project { get; set; }
}