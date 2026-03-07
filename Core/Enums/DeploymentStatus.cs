namespace Core.Enums;


/// <summary>
/// Defines the possible statuses of a deployment,
/// including Pending, InProgress, Succeeded, and Failed.
/// </summary>
public enum DeploymentStatus
{
    Pending,
    InProgress,
    Succeeded,
    Failed
}