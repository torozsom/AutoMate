using Application.Abstractions.Ai;

namespace Application.Ai;

/// <summary>Rejects fabricated or excluded evidence before provider return and result persistence.</summary>
public static class AnalysisEvidence
{
    /// <summary>Requires exact ordinal membership; an empty list remains valid when evidence is unavailable.</summary>
    public static LlmAnalysisResponse Validate(LlmAnalysisResponse response, IReadOnlyList<string>? allowed)
    {
        if (allowed is not null &&
            response.EvidenceReferences.Any(reference => !allowed.Contains(reference, StringComparer.Ordinal)))
            throw new InvalidAnalysisResultException();
        return response;
    }
}