using System.Text.RegularExpressions;
using Application.Abstractions.Ai.Context;
using Application.Abstractions.Ai.Results;
using Application.Abstractions.Diagnostics.Events;
using Application.Abstractions.Diagnostics.Redaction;
using Application.Ai.Policy;

namespace Application.Ai.Results;

/// <summary>
///     Validates legacy/v2 result shapes and redacts every provider-authored text field.
/// </summary>
/// <param name="redactor">The shared redaction boundary used before persistence or presentation.</param>
public sealed partial class AnalysisResultValidator(IDiagnosticRedactor redactor) : IAnalysisResultValidator
{
    /// <summary>Current version of the persisted structured result contract.</summary>
    public const int SchemaVersion = 2;

    /// <summary>Maximum summary size, compatible with the central redactor's terminal-text limit.</summary>
    private const int MaximumSummaryCharacters = 4096;

    /// <summary>Maximum number of remediation suggestions.</summary>
    private const int MaximumSteps = 10;

    /// <summary>Maximum size of one suggestion.</summary>
    private const int MaximumStepCharacters = 2048;

    /// <summary>Maximum number of source references.</summary>
    private const int MaximumEvidenceReferences = 20;

    /// <summary>Maximum size of one evidence reference.</summary>
    private const int MaximumEvidenceCharacters = 512;

    /// <summary>
    ///     Validates the complete result contract and redacts every provider-authored text field.
    /// </summary>
    /// <param name="response">The untrusted structured analysis response to validate.</param>
    /// <returns>An independent fully validated and redacted result; no partial output is accepted.</returns>
    /// <exception cref="InvalidAnalysisResultException">
    ///     The response or value violates the supported result, text or provenance contract.
    /// </exception>
    public LlmAnalysisResponse Validate(LlmAnalysisResponse response)
    {
        if (response is null || response.ResultSchemaVersion is not (null or 1 or SchemaVersion) ||
            response is { ResultSchemaVersion: SchemaVersion, Sections: null } ||
            response is { ResultSchemaVersion: 1, Sections: not null } ||
            response.InputTokens < 0 || response.OutputTokens < 0 ||
            response.EstimatedCost < 0 || response.EstimatedCost > 9_999_999_999.99999999m ||
            (response.EstimatedCost is { } cost && decimal.Round(cost, 8) != cost) ||
            response.EstimatedCost.HasValue != response.CostCurrency is not null ||
            (response.CostCurrency is { } currency && (currency.Length != 3 || currency.Any(c => c is < 'A' or > 'Z'))))
            throw new InvalidAnalysisResultException();

        // Materialize every field before the caller can mutate a tracked entity with any provider output.
        return response with
        {
            Provider = Identifier(response.Provider),
            Model = Identifier(response.Model),
            RequestedModel = OptionalIdentifier(response.RequestedModel),
            ModelVersion = OptionalIdentifier(response.ModelVersion),
            PromptVersion = OptionalIdentifier(response.PromptVersion),
            ResultSchemaVersion = response.Sections is null ? 1 : SchemaVersion,
            Sections = response.Sections is null
                ? null
                : new AssessmentSections(
                    Items(response.Sections.Observations, 6, 768), Items(response.Sections.MetricsAssessment, 6, 768),
                    Items(response.Sections.PotentialIssues, 6, 768), Items(response.Sections.Limitations, 6, 768)),
            Summary = response.Sections is not null &&
                      response.Summary is { Length: <= MaximumSummaryCharacters } overview &&
                      string.IsNullOrWhiteSpace(overview)
                ? "No overview was recorded for the selected evidence."
                : Text(response.Summary, MaximumSummaryCharacters),
            RecommendedSteps = Items(response.RecommendedSteps, MaximumSteps, MaximumStepCharacters),
            EvidenceReferences = Items(response.EvidenceReferences, MaximumEvidenceReferences,
                MaximumEvidenceCharacters)
        };
    }

    /// <summary>Reports whether a collection exceeds its supported item limit.</summary>
    private static bool ExceedsItemLimit(IReadOnlyCollection<string> values, int maximum)
    {
        return values.Count > maximum;
    }

    /// <summary>
    ///     Rejects oversized/null collections and redacts each element separately before JSON
    ///     serialization.
    /// </summary>
    /// <param name="values">The values to validate and copy.</param>
    /// <param name="count">The maximum allowed collection size.</param>
    /// <param name="length">The maximum permitted identifier or text length.</param>
    /// <returns>An independent collection of validated, individually redacted text values.</returns>
    /// <exception cref="InvalidAnalysisResultException">
    ///     The response or value violates the supported result, text or provenance contract.
    /// </exception>
    private string[] Items(IReadOnlyList<string> values, int count, int length)
    {
        if (values is null) throw new InvalidAnalysisResultException();
        if (ExceedsItemLimit(values, count))
            throw new InvalidAnalysisResultException(AnalysisResultRejection.FieldLimits);
        var safe = new string[values.Count];
        for (var index = 0; index < safe.Length; index++) safe[index] = Text(values[index], length);
        return safe;
    }

    /// <summary>
    ///     Validates both raw and redacted limits; no partially truncated result is accepted as
    ///     complete.
    /// </summary>
    /// <param name="value">The value to normalize, redact or validate.</param>
    /// <param name="maximum">The supported upper bound.</param>
    /// <returns>Validated and redacted text within the requested length bound.</returns>
    /// <exception cref="InvalidAnalysisResultException">
    ///     The response or value violates the supported result, text or provenance contract.
    /// </exception>
    private string Text(string value, int maximum)
    {
        if (string.IsNullOrWhiteSpace(value)) throw new InvalidAnalysisResultException();
        if (value.Length > maximum) throw new InvalidAnalysisResultException(AnalysisResultRejection.FieldLimits);

        var safe = redactor.Redact(new DeploymentDiagnosticEvent(Guid.Empty, null, DeploymentDiagnosticSource.AutoMate,
            DeploymentDiagnosticKind.Annotation, DeploymentDiagnosticSeverity.Information, DateTimeOffset.UtcNow,
            value, new DeploymentTerminalChannel(DeploymentTerminalChannelKind.System))).Event.Message.Trim();

        if (string.IsNullOrWhiteSpace(safe)) throw new InvalidAnalysisResultException();

        return safe.Length > maximum
            ? throw new InvalidAnalysisResultException(AnalysisResultRejection.FieldLimits)
            : safe;
    }

    /// <summary>
    ///     Accepts short non-secret provider/model/version identifiers rather than arbitrary
    ///     metadata payloads.
    /// </summary>
    /// <param name="value">The value to normalize, redact or validate.</param>
    /// <returns>A bounded provenance identifier unchanged by redaction.</returns>
    /// <exception cref="InvalidAnalysisResultException">
    ///     The response or value violates the supported result, text or provenance contract.
    /// </exception>
    private string Identifier(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 100)
            throw new InvalidAnalysisResultException(AnalysisResultRejection.InvalidProvenance);
        var safe = Text(value, 100);
        if (safe != value || safe.Contains("://", StringComparison.Ordinal) || !IdentifierPattern().IsMatch(safe))
            throw new InvalidAnalysisResultException(AnalysisResultRejection.InvalidProvenance);
        return safe;
    }

    /// <summary>
    ///     Leaves unknown provenance unknown rather than fabricating a model revision.
    /// </summary>
    /// <param name="value">The value to normalize, redact or validate.</param>
    /// <returns>The validated provenance identifier, or null when it was not supplied.</returns>
    /// <exception cref="InvalidAnalysisResultException">
    ///     The response or value violates the supported result, text or provenance contract.
    /// </exception>
    private string? OptionalIdentifier(string? value)
    {
        return value is null ? null : Identifier(value);
    }

    /// <summary>
    ///     Limits provenance to conventional model/provider identifiers, excluding URLs, secrets
    ///     and control text.
    /// </summary>
    /// <returns>The cached pattern for bounded non-secret provenance identifiers.</returns>
    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9._:/-]*$", RegexOptions.CultureInvariant)]
    private static partial Regex IdentifierPattern();
}

/// <summary>
///     Safe rejection of malformed, oversized or secret-bearing provenance; contains no
///     provider payload.
/// </summary>
/// <param name="reason">The finite rejection or admission-denial category.</param>
public sealed class InvalidAnalysisResultException(AnalysisResultRejection reason)
    : Exception("The provider analysis did not satisfy the result contract.")
{
    /// <summary>
    ///     Retains the original parameterless exception contract for existing callers.
    /// </summary>
    public InvalidAnalysisResultException() : this(AnalysisResultRejection.SchemaMismatch)
    {
    }

    /// <summary>Finite rejection category, containing no provider-authored text.</summary>
    public AnalysisResultRejection Reason { get; } = reason;

    /// <summary>Stable persisted code; undefined categories fail closed to the legacy rejection.</summary>
    public string FailureCode => Reason switch
    {
        AnalysisResultRejection.IncompleteOutput => "invalid_response_incomplete",
        AnalysisResultRejection.Refusal => "invalid_response_refusal",
        AnalysisResultRejection.MalformedEnvelope => "invalid_response_envelope",
        AnalysisResultRejection.MalformedJson => "invalid_response_json",
        AnalysisResultRejection.SchemaMismatch => "invalid_response_schema",
        AnalysisResultRejection.FieldLimits => "invalid_response_limits",
        AnalysisResultRejection.InvalidProvenance => "invalid_response_provenance",
        AnalysisResultRejection.UnknownEvidenceReference => "invalid_response_evidence",
        AnalysisResultRejection.ResponseTooLarge => "invalid_response_size",
        _ => "invalid_response"
    };

    /// <summary>
    ///     Fixed user guidance for retained and current validation failures; unknown codes return
    ///     no guidance.
    /// </summary>
    /// <param name="code">The fixed failure or skip code; unknown codes use safe fallback guidance.</param>
    /// <returns>Fixed actionable rejection guidance, or null for an unrecognized code.</returns>
    public static string? Guidance(string? code)
    {
        return code switch
        {
            "invalid_response_incomplete" =>
                "The provider did not finish its answer within the configured output allowance. Select a smaller context and request another analysis.",
            "invalid_response_refusal" =>
                "The provider declined this analysis. Review the selected diagnostic context before requesting another analysis.",
            "invalid_response_limits" or "invalid_response_size" =>
                "The provider answer exceeded the supported result limits. Select a smaller context and request another analysis.",
            "invalid_response_evidence" =>
                "The provider cited evidence outside the selected context. No answer was saved. Request another analysis.",
            "invalid_response_provenance" =>
                "The provider returned invalid result metadata. No answer was saved. Contact the operator if this persists.",
            "invalid_response" or "invalid_response_envelope" or "invalid_response_json" or "invalid_response_schema" =>
                "The provider answer did not match the supported result format. No answer was saved. Request another analysis; contact the operator if this persists.",
            _ => null
        };
    }
}

/// <summary>
///     Allowlisted result-rejection categories safe for persisted metadata and operational
///     diagnostics.
/// </summary>
public enum AnalysisResultRejection
{
    /// <summary>Provider did not complete its answer.</summary>
    IncompleteOutput,

    /// <summary>Provider explicitly refused to answer.</summary>
    Refusal,

    /// <summary>Responses envelope is malformed or unsupported.</summary>
    MalformedEnvelope,

    /// <summary>Response or answer JSON cannot be parsed.</summary>
    MalformedJson,

    /// <summary>Answer fields violate the result schema.</summary>
    SchemaMismatch,

    /// <summary>Answer fields exceed configured collection or text limits.</summary>
    FieldLimits,

    /// <summary>Provider/model provenance is invalid.</summary>
    InvalidProvenance,

    /// <summary>Answer cites an excluded or fabricated evidence reference.</summary>
    UnknownEvidenceReference,

    /// <summary>Response exceeds the bounded transport body limit.</summary>
    ResponseTooLarge
}

/// <summary>
///     Safe unavailable-provider classification, independent of raw provider exceptions.
/// </summary>
/// <param name="reason">The finite rejection or admission-denial category.</param>
public sealed class AnalysisProviderUnavailableException(AnalysisSkipReason reason = AnalysisSkipReason.Unavailable)
    : Exception("AI analysis is unavailable.")
{
    /// <summary>Finite safe skip reason; never a provider payload.</summary>
    public AnalysisSkipReason Reason { get; } = reason;
}
