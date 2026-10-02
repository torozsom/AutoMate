namespace Application.Abstractions.Ai;

public sealed record DeploymentAnalysisWorkItem(Guid AnalysisId, Guid DeploymentId);

/// <summary>Durable work boundary; dispatch is transactional with analysis creation.</summary>
public interface IDeploymentAnalysisQueue
{
    Task<DeploymentAnalysisWorkItem?> ClaimNextAsync(CancellationToken cancellationToken = default);
}
