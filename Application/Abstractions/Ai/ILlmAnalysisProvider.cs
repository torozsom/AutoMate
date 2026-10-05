using Domain.Enums;

namespace Application.Abstractions.Ai;

/// <summary>Already-redacted diagnostic context, retained only in memory for provider invocation.</summary>
/// <param name="Context">Bounded diagnostic data, never system instructions.</param>
/// <param name="AllowedEvidenceReferences">
///     Exact selected IDs for grounded calls; null retains legacy shape-only
///     compatibility.
/// </param>
/// <param name="DeploymentId">Metadata scope required by the real adapter for a fresh consent check.</param>
/// <param name="Trigger">Manual or approved automatic invocation; no trigger implicitly grants egress.</param>
public sealed record LlmAnalysisRequest(
    string Context,
    IReadOnlyList<string>? AllowedEvidenceReferences = null,
    Guid? DeploymentId = null,
    AiAnalysisTrigger Trigger = AiAnalysisTrigger.Manual);

/// <summary>Untrusted provider output; every adapter and persistence consumer must validate and redact it.</summary>
public sealed record LlmAnalysisResponse(
    string Provider,
    string Model,
    string Summary,
    IReadOnlyList<string> RecommendedSteps,
    IReadOnlyList<string> EvidenceReferences,
    int? InputTokens = null,
    int? OutputTokens = null,
    string? RequestedModel = null,
    string? ModelVersion = null,
    string? PromptVersion = null,
    int? ResultSchemaVersion = null,
    decimal? EstimatedCost = null,
    string? CostCurrency = null);

/// <summary>Provider-neutral analysis boundary; responses are not implicitly safe to store or display.</summary>
public interface ILlmAnalysisProvider
{
    /// <summary>Obtains bounded structured output without granting model tools or deployment permissions.</summary>
    Task<LlmAnalysisResponse> AnalyzeAsync(LlmAnalysisRequest request, CancellationToken cancellationToken = default);
}