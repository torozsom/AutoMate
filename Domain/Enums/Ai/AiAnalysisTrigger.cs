namespace Domain.Enums;

/// <summary>
///     Identifies how an AI deployment analysis was requested.
/// </summary>
public enum AiAnalysisTrigger
{
    /// <summary>
    ///     Automatically admitted after a recorded deployment failure, subject to policy and
    ///     consent.
    /// </summary>
    DeploymentFailed,

    /// <summary>Explicitly requested by the deployment owner.</summary>
    Manual
}
