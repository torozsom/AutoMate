using Application.Abstractions.Ai.Results;
using Application.Ai.Results;

namespace Application.Ai.Context;

/// <summary>
///     Rejects fabricated or excluded evidence before provider return and result persistence.
/// </summary>
public static class AnalysisEvidence
{
    /// <summary>
    ///     Requires exact ordinal membership; an empty list remains valid when evidence is
    ///     unavailable.
    /// </summary>
    /// <param name="response">The untrusted structured analysis response to validate.</param>
    /// <param name="allowed">
    ///     Exact selected evidence references; null leaves membership validation to the caller.
    /// </param>
    /// <returns>The original response after exact evidence membership has been verified.</returns>
    /// <exception cref="InvalidAnalysisResultException">
    ///     A response cites a reference outside the supplied evidence catalog.
    /// </exception>
    public static LlmAnalysisResponse Validate(LlmAnalysisResponse response, IReadOnlyList<string>? allowed)
    {
        if (allowed is not null &&
            response.EvidenceReferences.Any(reference => !allowed.Contains(reference, StringComparer.Ordinal)))
            throw new InvalidAnalysisResultException(AnalysisResultRejection.UnknownEvidenceReference);
        return response;
    }
}
