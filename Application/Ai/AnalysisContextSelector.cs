using System.Globalization;
using System.Text.Json;
using Application.Abstractions.Ai;
using Application.Abstractions.Diagnostics;

namespace Application.Ai;

/// <summary>
///     Deterministically selects diagnostic evidence by severity, failure relevance and recency within a bounded
///     window.
/// </summary>
public sealed class AnalysisContextSelector(IDiagnosticRedactor redactor)
{
    /// <summary>Caps CPU/memory work independently of provider text limits.</summary>
    public const int MaximumCandidates = 1000;

    /// <summary>Serializes a bounded structured data document with stable IDs and explicit partial coverage.</summary>
    public DeploymentAnalysisContext Select(DeploymentTerminalHistory history,
        IReadOnlyList<DeploymentMetricPoint> metrics,
        AnalysisContextBudget budget, bool metricsUnavailable, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        var rows = history.Events.Take(MaximumCandidates).Where(row => row.OrderId > 0)
            .GroupBy(row =>
                row.EventId is { } id && id != Guid.Empty
                    ? $"event:{id:N}"
                    : $"order:{row.OrderId.ToString(CultureInfo.InvariantCulture)}")
            .Select(group => redactor.RedactTerminal(group.OrderByDescending(row => row.OrderId).First()))
            .Where(row => !string.IsNullOrWhiteSpace(row.Message)).ToArray();
        var groups = rows.GroupBy(row => (row.Message, row.Severity, row.Stream))
            .Select(group => (
                Row: group.OrderByDescending(row => row.TimestampUtc).ThenByDescending(row => row.OrderId).First(),
                Count: group.Count(), First: group.Min(row => row.TimestampUtc),
                Last: group.Max(row => row.TimestampUtc)))
            .OrderByDescending(group => Severity(group.Row)).ThenByDescending(group => Relevant(group.Row.Message))
            .ThenByDescending(group => group.Row.TimestampUtc).ThenByDescending(group => group.Row.OrderId).ToArray();
        var selected = new List<Line>();
        var signals = new List<MetricSignal>();
        var traces = new List<TraceSignal>();
        var metricGroups = metrics.Take(1000).Where(SafeMetric).GroupBy(point => (point.Name, point.Unit)).Take(3)
            .ToArray();
        var availableTraces = rows.Where(row => ValidTrace(row.TraceId)).Select(row => row.TraceId).Distinct().Count();
        var logBudget = metricGroups.Length > 0 || availableTraces > 0
            ? budget with
            {
                Characters = Math.Max(1, budget.Characters * 3 / 4),
                Bytes = Math.Max(1, budget.Bytes * 3 / 4),
                Tokens = Math.Max(1, budget.Tokens * 3 / 4)
            }
            : budget;

        // Rebuild the complete document so coverage and escaping are charged before accepting any evidence.
        string Render()
        {
            return JsonSerializer.Serialize(new
            {
                schemaVersion = 1,
                coverage = new
                {
                    candidateEvents = rows.Length,
                    selectedGroups = selected.Count,
                    duplicateEvents = rows.Length - groups.Length,
                    omittedGroups = groups.Length - selected.Count,
                    earlierOmitted = history.EarlierOmitted || history.Events.Count > MaximumCandidates,
                    historyUnavailable = history.Availability is not null,
                    metricsUnavailable,
                    metricGroupsOmitted = metricGroups.Length - signals.Count,
                    traceGroupsOmitted = availableTraces - traces.Count
                },
                records = selected.OrderBy(line => line.timestamp).ThenBy(line => line.order).ToArray(),
                metricSignals = signals,
                traceSignals = traces
            });
        }

        foreach (var group in groups)
        {
            token.ThrowIfCancellationRequested();
            var row = group.Row;
            var message = redactor.RedactText(row.Message, 1024);
            var line = new Line(Reference(row), row.OrderId, row.TimestampUtc, row.Sequence,
                row.Severity is { } severity && Enum.IsDefined(severity) ? Enum.GetName(severity)! : "Unknown",
                message, group.Count, group.First, group.Last);
            selected.Add(line);
            // Shorten only message data, keeping the evidence identity and coverage document intact.
            while (!logBudget.Fits(Render()) && message.Length > 64)
            {
                message = redactor.RedactText(row.Message, message.Length / 2);
                selected[^1] = line with { message = message };
            }

            if (!logBudget.Fits(Render())) selected.RemoveAt(selected.Count - 1);
        }

        foreach (var group in metricGroups)
        {
            token.ThrowIfCancellationRequested();
            var points = group.ToArray();
            var first = points.Min(point => point.Timestamp);
            var last = points.Max(point => point.Timestamp);
            signals.Add(new MetricSignal($"metric:{group.Key.Name}:{first.UtcTicks}:{last.UtcTicks}", group.Key.Name,
                group.Key.Unit,
                first, last, points.Length, points.Min(point => point.Minimum), points.Max(point => point.Maximum)));
            if (!budget.Fits(Render())) signals.RemoveAt(signals.Count - 1);
        }

        var selectedIds = selected.Select(line => line.reference).ToHashSet(StringComparer.Ordinal);
        foreach (var group in rows.Where(row => selectedIds.Contains(Reference(row)) && ValidTrace(row.TraceId))
                     .GroupBy(row => row.TraceId).Take(20))
        {
            traces.Add(new TraceSignal($"trace:{group.Key}", group.Count()));
            if (!budget.Fits(Render())) traces.RemoveAt(traces.Count - 1);
        }

        if (selected.Count == 0 && signals.Count == 0) return new DeploymentAnalysisContext("", []);
        var text = Render();
        return new DeploymentAnalysisContext(text, Array.AsReadOnly(selected.Select(line => line.reference)
            .Concat(signals.Select(signal => signal.reference))
            .Concat(traces.Select(trace => trace.reference)).ToArray()));
    }

    /// <summary>Uses only stable durable event/order identities, never a provider cursor or user supplied label.</summary>
    private static string Reference(DeploymentTerminalLog row)
    {
        return row.EventId is { } id && id != Guid.Empty
            ? $"event:{id:N}"
            : $"order:{row.OrderId.ToString(CultureInfo.InvariantCulture)}";
    }

    /// <summary>Ranks defined severities; stderr without severity ranks as a warning.</summary>
    private static int Severity(DeploymentTerminalLog row)
    {
        return row.Severity is { } severity && Enum.IsDefined(severity)
            ? (int)severity
            : row.Stream == DeploymentDiagnosticStream.StandardError
                ? 3
                : 2;
    }

    /// <summary>Simple bounded relevance heuristic; log content never becomes instructions.</summary>
    private static bool Relevant(string text)
    {
        return new[] { "error", "fail", "exception", "timeout", "denied", "unable" }
            .Any(word => text.Contains(word, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Only finite, nonnegative known numeric series may enter the summarized context.</summary>
    private static bool SafeMetric(DeploymentMetricPoint point)
    {
        return (point.Name, point.Unit) is
               ("automate_cpu_usage_cores", "cores") or ("automate_memory_used_bytes", "bytes")
               or ("automate_memory_limit_bytes", "bytes") &&
               double.IsFinite(point.Minimum) && double.IsFinite(point.Maximum) && double.IsFinite(point.Average) &&
               point.Minimum >= 0 && point.Minimum <= point.Average && point.Average <= point.Maximum;
    }

    /// <summary>Accepts canonical nonzero trace IDs without arbitrary provider metadata.</summary>
    private static bool ValidTrace(string? trace)
    {
        return trace is { Length: 32 } && trace.Any(character => character != '0') && trace.All(Uri.IsHexDigit);
    }

    /// <summary>Bounded evidence group retaining the representative actual event and window timestamps.</summary>
    private sealed record Line(
        string reference,
        long order,
        DateTimeOffset? timestamp,
        long? sequence,
        string severity,
        string message,
        int occurrences,
        DateTimeOffset? firstTimestamp,
        DateTimeOffset? lastTimestamp);

    /// <summary>Range over returned aggregate points; does not fabricate individual samples or causal conclusions.</summary>
    private sealed record MetricSignal(
        string reference,
        string name,
        string unit,
        DateTimeOffset firstTimestamp,
        DateTimeOffset lastTimestamp,
        int returnedPoints,
        double minimum,
        double maximum);

    /// <summary>Correlation observed in selected diagnostics, not a fetched span or error-status claim.</summary>
    private sealed record TraceSignal(string reference, int selectedEvents);
}