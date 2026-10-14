using Application.Ai.Results;

namespace Application.Abstractions.Ai.Results;

/// <summary>
///     Common validation/redaction boundary for provider output before persistence or
///     presentation.
/// </summary>
public interface IAnalysisResultValidator
{
    /// <summary>
    ///     Returns a fully validated safe copy, or rejects the entire result without retaining
    ///     partial output.
    /// </summary>
    /// <param name="response">The untrusted structured analysis response to validate.</param>
    /// <returns>An independent fully validated and redacted result; no partial output is accepted.</returns>
    /// <exception cref="InvalidAnalysisResultException">
    ///     The response or value violates the supported result, text or provenance contract.
    /// </exception>
    LlmAnalysisResponse Validate(LlmAnalysisResponse response);
}
