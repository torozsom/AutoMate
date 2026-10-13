using Application.Ai;

namespace Application.Abstractions.Ai;

/// <summary>
///     Durably gates provider attempts by current ownership, shared concurrency and
///     conservative daily spend.
/// </summary>
public interface IAnalysisBudgetGuard
{
    /// <summary>
    ///     Returns a finite denial, or reserves this lease's maximum cost before one provider
    ///     invocation.
    /// </summary>
    /// <param name="work">The detached analysis claim and its lease ownership token.</param>
    /// <param name="cancellationToken">Cancels the operation when supported by the underlying work.</param>
    /// <returns>
    ///     A task returning null when the attempt reservation succeeds; otherwise a finite
    ///     admission-denial reason.
    /// </returns>
    Task<AnalysisSkipReason?> ReserveAttemptAsync(DeploymentAnalysisWorkItem work,
        CancellationToken cancellationToken = default);
}
