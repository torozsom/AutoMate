using Application.Abstractions.Ai;
using Application.Ai;
using Infrastructure.Diagnostics;

namespace Infrastructure.Tests.Ai;

/// <summary>Checks the provider-neutral safe-result boundary independently of transport and persistence.</summary>
public sealed class AnalysisResultTests
{
    /// <summary>Every text field is redacted before serialization; valid provenance, usage and optional costs survive.</summary>
    [Fact]
    public void Validation_returns_an_independent_redacted_copy()
    {
        var raw = Valid() with
        {
            Summary = "password=private-summary",
            RecommendedSteps = ["token=private-step"],
            EvidenceReferences = ["api_key=private-evidence"],
            ModelVersion = "2026-01-01",
            InputTokens = 123,
            OutputTokens = 45,
            EstimatedCost = 0.00001234m,
            CostCurrency = "USD"
        };
        var safe = Validator().Validate(raw);
        Assert.Equal("password=[REDACTED]", safe.Summary);
        Assert.Equal("token=[REDACTED]", Assert.Single(safe.RecommendedSteps));
        Assert.Equal("api_key=[REDACTED]", Assert.Single(safe.EvidenceReferences));
        Assert.Contains("private-summary", raw.Summary);
        Assert.NotSame(raw.RecommendedSteps, safe.RecommendedSteps);
        Assert.Equal(123, safe.InputTokens);
        Assert.Equal(0.00001234m, safe.EstimatedCost);
        Assert.Equal(1, safe.ResultSchemaVersion);
        Assert.Equal("2026-01-01", safe.ModelVersion);
    }

    /// <summary>Unknown usage, revision and cost remain unknown; an alias is never turned into an invented version.</summary>
    [Fact]
    public void Missing_metadata_is_preserved_as_null()
    {
        var safe = Validator().Validate(Valid());
        Assert.Null(safe.InputTokens);
        Assert.Null(safe.OutputTokens);
        Assert.Null(safe.ModelVersion);
        Assert.Null(safe.EstimatedCost);
        Assert.Null(safe.CostCurrency);
    }

    /// <summary>Rejects malformed output entirely rather than storing truncated or partially validated results.</summary>
    [Theory]
    [InlineData("empty-summary")]
    [InlineData("oversized-summary")]
    [InlineData("null-steps")]
    [InlineData("too-many-steps")]
    [InlineData("null-step")]
    [InlineData("oversized-step")]
    [InlineData("oversized-evidence")]
    [InlineData("too-many-evidence")]
    [InlineData("negative-tokens")]
    [InlineData("negative-cost")]
    [InlineData("cost-without-currency")]
    [InlineData("cost-precision")]
    [InlineData("secret-provenance")]
    [InlineData("url-provenance")]
    [InlineData("unsupported-schema")]
    [InlineData("control-only-summary")]
    public void Malformed_results_have_a_safe_rejection(string kind)
    {
        var raw = kind switch
        {
            "empty-summary" => Valid() with { Summary = " " },
            "oversized-summary" => Valid() with { Summary = new string('x', 4097) },
            "null-steps" => Valid() with { RecommendedSteps = null! },
            "too-many-steps" => Valid() with { RecommendedSteps = Enumerable.Repeat("step", 11).ToArray() },
            "null-step" => Valid() with { RecommendedSteps = [null!] },
            "oversized-step" => Valid() with { RecommendedSteps = [new string('x', 2049)] },
            "oversized-evidence" => Valid() with { EvidenceReferences = [new string('x', 513)] },
            "too-many-evidence" => Valid() with { EvidenceReferences = Enumerable.Repeat("reference", 21).ToArray() },
            "negative-tokens" => Valid() with { InputTokens = -1 },
            "negative-cost" => Valid() with { EstimatedCost = -1m, CostCurrency = "USD" },
            "cost-without-currency" => Valid() with { EstimatedCost = 1m },
            "cost-precision" => Valid() with { EstimatedCost = 0.000000001m, CostCurrency = "USD" },
            "secret-provenance" => Valid() with { Model = "password=private-value" },
            "url-provenance" => Valid() with { Model = "https://provider.invalid/private-value" },
            "unsupported-schema" => Valid() with { ResultSchemaVersion = 2 },
            "control-only-summary" => Valid() with { Summary = "\u001b[2J" },
            _ => throw new ArgumentOutOfRangeException(nameof(kind))
        };
        var error = Assert.Throws<InvalidAnalysisResultException>(() => Validator().Validate(raw));
        Assert.DoesNotContain("private-value", error.ToString());
    }

    /// <summary>Creates the production validator with central redaction.</summary>
    internal static AnalysisResultValidator Validator()
    {
        return new AnalysisResultValidator(new DiagnosticRedactor());
    }

    /// <summary>Creates a minimal conforming provider result.</summary>
    internal static LlmAnalysisResponse Valid()
    {
        return new LlmAnalysisResponse("openai", "gpt-5-mini-2025-08-07", "A dependency could not start.",
            ["Check dependency configuration."], ["container/web startup"], RequestedModel: "gpt-5-mini",
            PromptVersion: "deployment-diagnostics-v1");
    }
}