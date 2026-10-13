using Application.Ai;
using Domain.Enums;

namespace Application.Abstractions.Ai;

/// <summary>
///     Already-redacted diagnostic context, retained only in memory for provider invocation.
/// </summary>
/// <param name="Context">Bounded diagnostic data, never system instructions.</param>
/// <param name="AllowedEvidenceReferences">
///     Exact selected IDs for grounded calls; null retains legacy shape-only compatibility.
/// </param>
/// <param name="DeploymentId">
///     Metadata scope required by the real adapter for a fresh consent check.
/// </param>
/// <param name="Trigger">
///     Manual or approved automatic invocation; no trigger implicitly grants egress.
/// </param>
/// <param name="Kind">The assessment focus requested or resolved for the deployment.</param>
public sealed record LlmAnalysisRequest(
    string Context,
    IReadOnlyList<string>? AllowedEvidenceReferences = null,
    Guid? DeploymentId = null,
    AiAnalysisTrigger Trigger = AiAnalysisTrigger.Manual,
    AssessmentKind Kind = AssessmentKind.Automatic);

/// <summary>
///     Untrusted provider output; every adapter and persistence consumer must validate and
///     redact it.
/// </summary>
/// <param name="Provider">The provider identity; not a credential or endpoint.</param>
/// <param name="Model">The provider-returned model identity.</param>
/// <param name="Summary">
///     The analysis overview; provider-authored text remains untrusted until validation.
/// </param>
/// <param name="RecommendedSteps">
///     Bounded diagnostic suggestions; these are never executed automatically.
/// </param>
/// <param name="EvidenceReferences">Exact selected identifiers cited by the analysis.</param>
/// <param name="InputTokens">The provider-reported input-token count, if known.</param>
/// <param name="OutputTokens">The provider-reported output-token count, if known.</param>
/// <param name="RequestedModel">The configured model or deployment identity used for the request.</param>
/// <param name="ModelVersion">The explicit model revision, if supplied by the provider.</param>
/// <param name="PromptVersion">
///     The AutoMate trusted prompt version used for the request, if recorded.
/// </param>
/// <param name="ResultSchemaVersion">
///     The persisted result contract version; absent legacy metadata remains unknown.
/// </param>
/// <param name="EstimatedCost">
///     The provider-reported estimated cost, if supplied; not a billing guarantee.
/// </param>
/// <param name="CostCurrency">The uppercase three-letter currency of the estimate, if known.</param>
/// <param name="Sections">Validated version-two assessment sections, if present.</param>
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
    string? CostCurrency = null,
    AssessmentSections? Sections = null);

/// <summary>
///     Provider-neutral analysis boundary; responses are not implicitly safe to store or
///     display.
/// </summary>
public interface ILlmAnalysisProvider
{
    /// <summary>
    ///     Obtains bounded structured output without granting model tools or deployment
    ///     permissions.
    /// </summary>
    /// <param name="request">The redacted evidence request and deployment egress scope.</param>
    /// <param name="cancellationToken">Cancels the operation when supported by the underlying work.</param>
    /// <returns>
    ///     A task returning untrusted structured analysis output that must be validated and
    ///     redacted before use.
    /// </returns>
    /// <exception cref="AnalysisProviderUnavailableException">
    ///     Current provider configuration, operator policy or deployment consent prevents execution.
    /// </exception>
    /// <exception cref="AnalysisProviderAccessDeniedException">
    ///     The provider rejects access to the configured resource or model.
    /// </exception>
    /// <exception cref="TransientAnalysisProviderException">
    ///     The provider or transport reports a failure eligible for bounded retry handling.
    /// </exception>
    /// <exception cref="InvalidAnalysisResultException">
    ///     The provider output violates the supported response, provenance or evidence contract.
    /// </exception>
    /// <exception cref="OperationCanceledException">The caller cancels the provider request.</exception>
    /// <exception cref="ArgumentException">The supplied context exceeds configured input limits.</exception>
    /// <exception cref="HttpRequestException">The provider reports a permanent HTTP failure.</exception>
    Task<LlmAnalysisResponse> AnalyzeAsync(LlmAnalysisRequest request, CancellationToken cancellationToken = default);
}
