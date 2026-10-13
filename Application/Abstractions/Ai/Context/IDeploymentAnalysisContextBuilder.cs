using Application.Ai;

namespace Application.Abstractions.Ai;

/// <summary>
///     Builds redacted, bounded, in-memory evidence context without persisting diagnostic
///     snapshots.
/// </summary>
public interface IDeploymentAnalysisContextBuilder
{
    /// <summary>
    ///     Reads a bounded deployment-scoped diagnostic window with explicit omission/availability
    ///     metadata.
    /// </summary>
    /// <param name="deploymentId">The exact deployment identity used for ownership or correlation.</param>
    /// <param name="cancellationToken">Cancels the operation when supported by the underlying work.</param>
    /// <returns>
    ///     A task returning bounded redacted evidence text, permitted references and collection
    ///     provenance.
    /// </returns>
    Task<DeploymentAnalysisContext> BuildAsync(Guid deploymentId, CancellationToken cancellationToken = default);

    /// <summary>
    ///     Collects an explicitly selected context; default supports existing test/provider
    ///     implementations.
    /// </summary>
    /// <param name="deploymentId">The exact deployment identity used for ownership or correlation.</param>
    /// <param name="selection">The immutable source, container, focus and time-range options.</param>
    /// <param name="cancellationToken">Cancels the operation when supported by the underlying work.</param>
    /// <returns>
    ///     A task returning bounded redacted evidence text, permitted references and collection
    ///     provenance.
    /// </returns>
    /// <remarks>
    ///     The compatibility implementation delegates to the legacy overload; selection-aware
    ///     adapters must override it.
    /// </remarks>
    Task<DeploymentAnalysisContext> BuildAsync(Guid deploymentId, AssessmentSelection selection,
        CancellationToken cancellationToken = default)
    {
        return BuildAsync(deploymentId, cancellationToken);
    }
}

/// <summary>
///     Selected context and the exact evidence identifiers a provider is permitted to cite.
/// </summary>
/// <param name="Text">The diagnostic context or text being processed.</param>
/// <param name="EvidenceReferences">Exact selected identifiers cited by the analysis.</param>
/// <param name="Provenance">Recorded evidence selection and collection metadata, if available.</param>
public sealed record DeploymentAnalysisContext(
    string Text,
    IReadOnlyList<string> EvidenceReferences,
    AssessmentProvenance? Provenance = null);
