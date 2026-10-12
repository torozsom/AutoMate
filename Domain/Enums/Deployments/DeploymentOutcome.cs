namespace Domain.Enums;

/// <summary>
///     Deployment completion outcome, independent of later runtime stops.
/// </summary>
public enum DeploymentOutcome
{
    /// <summary>No reliable completion evidence is available.</summary>
    Unknown,

    /// <summary>Deployment preparation or the remote workflow completed successfully.</summary>
    Succeeded,

    /// <summary>Deployment preparation or the remote workflow failed.</summary>
    Failed
}