namespace Application.Abstractions.Ai;

/// <summary>Reports local AI configuration and durable metadata access without claiming work or contacting a provider.</summary>
public interface IDeploymentAnalysisReadiness
{
    /// <summary>Reads current configuration and bounded queue metadata; cancellation reaches the database operation.</summary>
    Task<DeploymentAnalysisReadiness> CheckAsync(CancellationToken cancellationToken = default);
}

/// <summary>Finite operator-facing configuration state; readiness never grants deployment consent or egress approval.</summary>
public enum AnalysisConfigurationState
{
    /// <summary>AI admission is intentionally disabled.</summary>
    Disabled,

    /// <summary>AI is enabled but the independent provider-egress switch is closed.</summary>
    EgressDisabled,

    /// <summary>Approved routing and locally configured credentials are present, without remote verification.</summary>
    Ready,

    /// <summary>Required provider approvals, routing or credentials are absent.</summary>
    Unavailable,

    /// <summary>Current typed configuration fails validation.</summary>
    Invalid
}

/// <summary>Safe readiness snapshot containing no identities, credentials, diagnostic payloads or provider responses.</summary>
/// <param name="Configuration">Current local configuration state.</param>
/// <param name="QueueAvailable">Whether queue, analysis, automatic-wakeup and budget metadata can be read.</param>
public sealed record DeploymentAnalysisReadiness(AnalysisConfigurationState Configuration, bool QueueAvailable);