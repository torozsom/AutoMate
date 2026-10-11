namespace Application.Abstractions.Ai;

/// <summary>Common validation/redaction boundary for provider output before persistence or presentation.</summary>
public interface IAnalysisResultValidator
{
    /// <summary>Returns a fully validated safe copy, or rejects the entire result without retaining partial output.</summary>
    LlmAnalysisResponse Validate(LlmAnalysisResponse response);
}