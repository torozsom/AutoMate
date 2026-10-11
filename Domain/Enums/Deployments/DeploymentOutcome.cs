namespace Domain.Enums;

/// <summary>Deployment completion outcome, independent of later runtime stops.</summary>
public enum DeploymentOutcome
{
    /// <summary>No reliable completion evidence is available.</summary>
    Unknown,

    /// <summary>The deployment reached Running successfully.</summary>
    Succeeded,

    /// <summary>The deployment failed before becoming operational.</summary>
    Failed
}