using Application.Ai;

namespace Application.Abstractions.Ai;

/// <summary>Durably gates provider attempts by current ownership, shared concurrency and conservative daily spend.</summary>
public interface IAnalysisBudgetGuard
{
    /// <summary>Returns a finite denial, or reserves this lease's maximum cost before one provider invocation.</summary>
    Task<AnalysisSkipReason?> ReserveAttemptAsync(DeploymentAnalysisWorkItem work,
        CancellationToken cancellationToken = default);
}