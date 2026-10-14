namespace Domain.Enums.Deployments;

/// <summary>
///     Defines the possible statuses of a deployment.
/// </summary>
public enum DeploymentStatus
{
    /// <summary>Recorded deployment startup or preparation is in progress.</summary>
    Starting,

    /// <summary>Recorded running state; it does not itself establish application health.</summary>
    Running,

    /// <summary>Recorded inactive runtime state after shutdown.</summary>
    Stopped,

    /// <summary>Recorded failure during deployment or runtime processing.</summary>
    Failed
}
