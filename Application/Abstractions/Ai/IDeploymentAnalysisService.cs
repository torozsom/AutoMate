using Domain.Enums;

namespace Application.Abstractions.Ai;

public sealed record DeploymentAnalysisView(
    Guid Id,
    Guid DeploymentId,
    AiAnalysisStatus Status,
    AiAnalysisTrigger Trigger,
    string? Summary,
    IReadOnlyList<string> RecommendedSteps,
    IReadOnlyList<string> EvidenceReferences,
    string? FailureCode,
    DateTimeOffset CreatedAt,
    DateTimeOffset? CompletedAt);

public sealed record DeploymentAnalysisRequestResult(
    bool Accepted,
    string Message,
    DeploymentAnalysisView? Analysis = null);

/// <summary>Owner-authorized use case for requesting and reading deployment diagnoses.</summary>
public interface IDeploymentAnalysisService
{
    Task<DeploymentAnalysisRequestResult> RequestManualAsync(Guid ownerId, Guid deploymentId,
        CancellationToken cancellationToken = default);

    Task<DeploymentAnalysisView?> GetLatestAsync(Guid ownerId, Guid deploymentId,
        CancellationToken cancellationToken = default);
}