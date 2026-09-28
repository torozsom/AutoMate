namespace Application.Abstractions.Diagnostics;

/// <summary>
/// Accepts normalized deployment observations for redaction, telemetry, bounded buffering, and safe UI delivery.
/// </summary>
public interface IDeploymentDiagnosticPublisher
{
    /// <summary>Publishes a diagnostic without allowing a slow downstream consumer to block deployment work.</summary>
    ValueTask PublishAsync(DeploymentDiagnosticEvent diagnosticEvent, CancellationToken cancellationToken = default);
}
