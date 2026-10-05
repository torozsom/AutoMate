namespace Application.Abstractions.Ai;

/// <summary>Builds redacted, bounded, in-memory evidence context without persisting diagnostic snapshots.</summary>
public interface IDeploymentAnalysisContextBuilder
{
    /// <summary>Reads a bounded deployment-scoped diagnostic window with explicit omission/availability metadata.</summary>
    Task<DeploymentAnalysisContext> BuildAsync(Guid deploymentId, CancellationToken cancellationToken = default);
}

/// <summary>Selected context and the exact evidence identifiers a provider is permitted to cite.</summary>
public sealed record DeploymentAnalysisContext(string Text, IReadOnlyList<string> EvidenceReferences);