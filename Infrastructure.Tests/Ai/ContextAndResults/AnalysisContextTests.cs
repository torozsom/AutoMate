using System.Text.Json;
using Application.Abstractions.Diagnostics;
using Application.Ai;
using Infrastructure.Diagnostics;

namespace Infrastructure.Tests.Ai;

/// <summary>Verifies selection, serialized budget accounting and exact evidence grounding with synthetic diagnostics.</summary>
public sealed class AnalysisContextTests
{
    /// <summary>Old errors remain ahead of recent chatter; long messages shrink without excluding subsequent evidence.</summary>
    [Fact]
    public void Severity_and_relevance_take_priority_over_recent_chatter_and_oversized_lines()
    {
        var rows = new[]
        {
            Row(1, "fatal failure " + new string('x', 4000), DeploymentDiagnosticSeverity.Error),
            Row(2, "ready"), Row(3, "unable to connect", DeploymentDiagnosticSeverity.Warning)
        };
        var budget = new AnalysisContextBudget(2000, 2000, 1300);
        var context = Selector().Select(new DeploymentTerminalHistory(rows, true), [], budget, false);
        Assert.True(budget.Fits(context.Text));
        Assert.Contains("order:1", context.EvidenceReferences);
        using var document = JsonDocument.Parse(context.Text);
        Assert.True(document.RootElement.GetProperty("coverage").GetProperty("earlierOmitted").GetBoolean());
        Assert.Contains("fatal failure", context.Text);
    }

    /// <summary>
    ///     Repeated redacted lines collapse with window counts while retaining a real representative event
    ///     ID/time/sequence.
    /// </summary>
    [Fact]
    public void Duplicate_groups_have_stable_references_counts_and_chronological_rendering()
    {
        var id = Guid.NewGuid();
        var rows = new[]
        {
            Row(1, "password=private-a"), Row(2, "password=private-b") with { EventId = id, Sequence = 42 },
            Row(3, "new failure", DeploymentDiagnosticSeverity.Error)
        };
        var context = Selector().Select(new DeploymentTerminalHistory(rows, false), [], Budget(), false);
        var repeated = Selector().Select(new DeploymentTerminalHistory(rows.Reverse().ToArray(), false), [], Budget(),
            false);
        Assert.Equal(context.Text, repeated.Text);
        Assert.DoesNotContain("private", context.Text);
        using var document = JsonDocument.Parse(context.Text);
        var records = document.RootElement.GetProperty("records").EnumerateArray().ToArray();
        Assert.Equal(2, records.Length);
        Assert.Equal(2, records[0].GetProperty("occurrences").GetInt32());
        Assert.Equal(42, records[0].GetProperty("sequence").GetInt64());
        Assert.Equal($"event:{id:N}", records[0].GetProperty("reference").GetString());
        Assert.DoesNotContain("order:1", context.EvidenceReferences);
    }

    /// <summary>Encoded Unicode and escaping are charged before selection; small limits cannot emit malformed partial JSON.</summary>
    [Theory]
    [InlineData(1)]
    [InlineData(1000)]
    [InlineData(2000)]
    public void Unicode_context_respects_all_limits_and_preserves_complete_json(int limit)
    {
        var budget = new AnalysisContextBudget(limit, limit, limit);
        var context = Selector()
            .Select(new DeploymentTerminalHistory([Row(1, string.Concat(Enumerable.Repeat("错误🙂\"", 1000)))], false),
                [], budget, false);
        if (context.Text.Length > 0)
        {
            Assert.True(budget.Fits(context.Text));
            using var document = JsonDocument.Parse(context.Text);
            Assert.Single(context.EvidenceReferences);
        }
        else
        {
            Assert.Empty(context.EvidenceReferences);
        }
    }

    /// <summary>
    ///     Summaries contain only actual numeric ranges and selected trace correlation, excluding metric labels and
    ///     invalid points.
    /// </summary>
    [Fact]
    public void Metric_and_trace_evidence_are_bounded_and_do_not_claim_missing_samples_or_spans()
    {
        var trace = "1234567890abcdef1234567890abcdef";
        var row = Row(1, "failure") with { TraceId = trace };
        var points = new[]
        {
            new DeploymentMetricPoint("private-container", "automate_cpu_usage_cores", "cores", row.TimestampUtc!.Value,
                2, 1, 3),
            new DeploymentMetricPoint("private-container", "private-metric", "private-unit", row.TimestampUtc.Value, 1,
                1, 1),
            new DeploymentMetricPoint("private-container", "automate_memory_used_bytes", "bytes",
                row.TimestampUtc.Value, double.NaN, 0, 1)
        };
        var context = Selector().Select(new DeploymentTerminalHistory([row], false, "private-provider-error"), points,
            Budget(), true);
        Assert.DoesNotContain("private", context.Text);
        Assert.Contains($"trace:{trace}", context.EvidenceReferences);
        Assert.Contains(context.EvidenceReferences,
            reference => reference.StartsWith("metric:automate_cpu_usage_cores:", StringComparison.Ordinal));
        using var document = JsonDocument.Parse(context.Text);
        var signal = document.RootElement.GetProperty("metricSignals").EnumerateArray().Single();
        Assert.Equal(1, signal.GetProperty("minimum").GetDouble());
        Assert.Equal(3, signal.GetProperty("maximum").GetDouble());
        Assert.True(document.RootElement.GetProperty("coverage").GetProperty("historyUnavailable").GetBoolean());
    }

    /// <summary>Cancellation propagates before reading data or rendering a provider request.</summary>
    [Fact]
    public void Cancelled_selection_does_not_produce_context()
    {
        Assert.Throws<OperationCanceledException>(() => Selector().Select(new DeploymentTerminalHistory([], false), [],
            Budget(), false, new CancellationToken(true)));
    }

    /// <summary>References must resolve exactly to selected evidence; case changes and excluded IDs are rejected.</summary>
    [Theory]
    [InlineData("order:1", true)]
    [InlineData("order:2", false)]
    [InlineData("ORDER:1", false)]
    public void Evidence_requires_exact_membership(string reference, bool valid)
    {
        var response = AnalysisResultTests.Valid() with { EvidenceReferences = [reference] };
        if (valid) Assert.Same(response, AnalysisEvidence.Validate(response, ["order:1"]));
        else Assert.Throws<InvalidAnalysisResultException>(() => AnalysisEvidence.Validate(response, ["order:1"]));
    }

    /// <summary>Creates the shared selector with actual secret masking.</summary>
    private static AnalysisContextSelector Selector()
    {
        return new AnalysisContextSelector(new DiagnosticRedactor());
    }

    /// <summary>Generous bounded test budget.</summary>
    private static AnalysisContextBudget Budget()
    {
        return new AnalysisContextBudget(24000, 48000, 12000);
    }

    /// <summary>Creates a durable synthetic event with stable time ordering.</summary>
    private static DeploymentTerminalLog Row(long order, string message,
        DeploymentDiagnosticSeverity severity = DeploymentDiagnosticSeverity.Information)
    {
        return new DeploymentTerminalLog(order, Guid.Empty, Guid.Empty, "web", message, Severity: severity,
            TimestampUtc: DateTimeOffset.UnixEpoch.AddSeconds(order));
    }
}