namespace Core.Enums;

/// <summary>
///     Defines the possible statuses of a deployment.
/// </summary>
public enum DeploymentStatus
{
    /// <summary>
    ///     Represents the deployment status where the process is in the initial stage of being started.
    ///     This state indicates that the deployment has been initiated but is not yet actively running.
    /// </summary>
    Starting,

    /// <summary>
    ///     Represents the state of a deployment that is currently running.
    ///     Indicates that the deployment process has been successfully initiated
    ///     and is actively operating or executing as intended.
    /// </summary>
    Running,

    /// <summary>
    ///     Represents the state of a deployment that has been manually
    ///     or programmatically stopped after being previously started.
    /// </summary>
    Stopped,

    /// <summary>
    ///     Represents a deployment status where the deployment process has failed.
    ///     This status indicates that an error or issue occurred during the deployment
    ///     lifecycle, and the deployment did not complete successfully.
    /// </summary>
    Failed
}