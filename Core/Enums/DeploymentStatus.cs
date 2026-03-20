namespace Core.Enums;

/// <summary>
///     Defines the possible statuses of a deployment,
///     including Pending, InProgress, Succeeded, and Failed.
/// </summary>
public enum DeploymentStatus
{
    /// <summary>
    /// The deployment is pending and has not yet started.
    /// </summary>
    Pending,

    /// <summary>
    /// The deployment is currently in progress.
    /// </summary>
    InProgress,

    /// <summary>
    /// The deployment has completed successfully.
    /// </summary>
    Succeeded,

    /// <summary>
    /// The deployment failed to complete.
    /// </summary>
    Failed
}