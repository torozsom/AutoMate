using System.Text.RegularExpressions;
using Application.Abstractions.Ai;
using Application.Abstractions.Diagnostics;

namespace Application.Ai;

/// <summary>Validates legacy/v2 result shapes and redacts every provider-authored text field.</summary>
public sealed partial class AnalysisResultValidator(IDiagnosticRedactor redactor) : IAnalysisResultValidator
{
    /// <summary>Current version of the persisted structured result contract.</summary>
    public const int SchemaVersion = 2;

    /// <summary>Maximum summary size, compatible with the central redactor's terminal-text limit.</summary>
    public const int MaximumSummaryCharacters = 4096;

    /// <summary>Maximum number of remediation suggestions.</summary>
    public const int MaximumSteps = 10;

    /// <summary>Maximum size of one suggestion.</summary>
    public const int MaximumStepCharacters = 2048;

    /// <summary>Maximum number of source references.</summary>
    public const int MaximumEvidenceReferences = 20;

    /// <summary>Maximum size of one evidence reference.</summary>
    public const int MaximumEvidenceCharacters = 512;

    /// <inheritdoc />
    public LlmAnalysisResponse Validate(LlmAnalysisResponse response)
    {
        if (response is null || response.ResultSchemaVersion is not (null or 1 or SchemaVersion) ||
            (response.ResultSchemaVersion == SchemaVersion && response.Sections is null) ||
            (response.ResultSchemaVersion == 1 && response.Sections is not null) ||
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

    /// <summary>Rejects oversized/null collections and redacts each element separately before JSON serialization.</summary>
    private string[] Items(IReadOnlyList<string> values, int count, int length)
    {
        if (values is null || values.Count < 0 || values.Count > count) throw new InvalidAnalysisResultException();
        var safe = new string[values.Count];
        for (var index = 0; index < safe.Length; index++) safe[index] = Text(values[index], length);
        return safe;
    }

    /// <summary>Validates both raw and redacted limits; no partially truncated result is accepted as complete.</summary>
    private string Text(string value, int maximum)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > maximum) throw new InvalidAnalysisResultException();
        var safe = redactor.Redact(new DeploymentDiagnosticEvent(Guid.Empty, null, DeploymentDiagnosticSource.AutoMate,
            DeploymentDiagnosticKind.Annotation, DeploymentDiagnosticSeverity.Information, DateTimeOffset.UtcNow,
            value, new DeploymentTerminalChannel(DeploymentTerminalChannelKind.System))).Event.Message.Trim();
        if (string.IsNullOrWhiteSpace(safe) || safe.Length > maximum) throw new InvalidAnalysisResultException();
        return safe;
    }

    /// <summary>Accepts short non-secret provider/model/version identifiers rather than arbitrary metadata payloads.</summary>
    private string Identifier(string value)
    {
        var safe = Text(value, 100);
        if (safe != value || safe.Contains("://", StringComparison.Ordinal) || !IdentifierPattern().IsMatch(safe))
            throw new InvalidAnalysisResultException();
        return safe;
    }

    /// <summary>Leaves unknown provenance unknown rather than fabricating a model revision.</summary>
    private string? OptionalIdentifier(string? value)
    {
        return value is null ? null : Identifier(value);
    }

    /// <summary>Limits provenance to conventional model/provider identifiers, excluding URLs, secrets and control text.</summary>
    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9._:/-]*$", RegexOptions.CultureInvariant)]
    private static partial Regex IdentifierPattern();
}

/// <summary>Safe rejection of malformed, oversized or secret-bearing provenance; contains no provider payload.</summary>
public sealed class InvalidAnalysisResultException()
    : Exception("The provider analysis did not satisfy the result contract.");

/// <summary>Safe unavailable-provider classification, independent of raw provider exceptions.</summary>
public sealed class AnalysisProviderUnavailableException(AnalysisSkipReason reason = AnalysisSkipReason.Unavailable)
    : Exception("AI analysis is unavailable.")
{
    /// <summary>Finite safe skip reason; never a provider payload.</summary>
    public AnalysisSkipReason Reason { get; } = reason;
}