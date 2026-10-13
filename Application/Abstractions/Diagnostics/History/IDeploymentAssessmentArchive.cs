using Application.Abstractions.Ai;

namespace Application.Abstractions.Diagnostics;

/// <summary>
///     Guarantees filtered archive assessment reads independently of the wider legacy archive compatibility surface.
/// </summary>
public interface IDeploymentAssessmentArchive
{
    /// <summary>
    ///     Filters archived evidence before applying the bounded result limit.
    /// </summary>
    /// <param name="query">The frozen owner/project/deployment scope and assessment selection.</param>
    /// <param name="token">Cancels the bounded archive read.</param>
    /// <returns>Selected evidence and recorded source metadata; adapter failures propagate.</returns>
    Task<ArchiveAssessmentPage> ReadAssessmentAsync(ArchiveAssessmentQuery query, CancellationToken token);

    /// <summary>
    ///     Filters recorded container identities before bounded metric aggregation.
    /// </summary>
    /// <param name="query">The frozen owner/project/deployment scope and metric selection.</param>
    /// <param name="token">Cancels the bounded metric read.</param>
    /// <returns>The selected recorded metric aggregates; adapter failures propagate.</returns>
    Task<IReadOnlyList<DeploymentMetricPoint>> ReadAssessmentMetricsAsync(ArchiveAssessmentQuery query,
        CancellationToken token);
}
