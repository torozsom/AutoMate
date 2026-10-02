namespace Application.Abstractions.Ai;

public sealed record LlmAnalysisRequest(string Context);
public sealed record LlmAnalysisResponse(string Provider, string Model, string Summary,
    IReadOnlyList<string> RecommendedSteps, IReadOnlyList<string> EvidenceReferences,
    int? InputTokens = null, int? OutputTokens = null);

public interface ILlmAnalysisProvider
{
    Task<LlmAnalysisResponse> AnalyzeAsync(LlmAnalysisRequest request, CancellationToken cancellationToken = default);
}
