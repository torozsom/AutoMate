namespace Application.Abstractions.Diagnostics;

/// <summary>Persists redacted deployment diagnostics and builds bounded, safe diagnostic context.</summary>
public interface IDeploymentDiagnosticStore
{
    Task PersistAsync(DeploymentDiagnosticEvent diagnosticEvent, CancellationToken cancellationToken = default);

    Task<string> BuildContextAsync(Guid deploymentId, int maximumCharacters,
        CancellationToken cancellationToken = default);
}