using Application.Abstractions.Diagnostics;

namespace Application.Abstractions.Ai;

public interface IDeploymentDiagnosticStore
{
    Task PersistAsync(DeploymentDiagnosticEvent diagnosticEvent, CancellationToken cancellationToken = default);
    Task<string> BuildContextAsync(Guid deploymentId, int maximumCharacters, CancellationToken cancellationToken = default);
}
