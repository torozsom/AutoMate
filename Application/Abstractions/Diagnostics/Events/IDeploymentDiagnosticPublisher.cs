namespace Application.Abstractions.Diagnostics;

/// <summary>
///     Accepts normalized deployment observations for redaction, telemetry, bounded buffering,
///     and safe UI delivery.
/// </summary>
public interface IDeploymentDiagnosticPublisher
{
    /// <summary>
    ///     Publishes a diagnostic without allowing a slow downstream consumer to block deployment
    ///     work.
    /// </summary>
    /// <param name="diagnosticEvent">The normalized diagnostic observation to process.</param>
    /// <param name="cancellationToken">Cancels the operation when supported by the underlying work.</param>
    /// <returns>A task that completes after the operation finishes.</returns>
    ValueTask PublishAsync(DeploymentDiagnosticEvent diagnosticEvent, CancellationToken cancellationToken = default);
}