using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Application.Abstractions.Ai;
using Application.Abstractions.Diagnostics;

namespace Application.Ai;

/// <summary>
///     Selects bounded diagnostic evidence using assessment focus, source representation, severity and recency.
/// </summary>
public sealed class AnalysisContextSelector(IDiagnosticRedactor redactor)
{
    /// <summary>Caps CPU/memory work independently of provider text limits.</summary>
    public const int MaximumCandidates = 1000;

    /// <summary>Serializes a bounded structured data document with stable IDs and explicit partial coverage.</summary>
    public DeploymentAnalysisContext Select(DeploymentTerminalHistory history,
        IReadOnlyList<DeploymentMetricPoint> metrics,
        AnalysisContextBudget budget, bool metricsUnavailable, CancellationToken token = default,
        AssessmentKind kind = AssessmentKind.FailureDiagnosis, object? metadata = null)
    {
        token.ThrowIfCancellationRequested();
        var rows = history.Events.Take(MaximumCandidates).Where(row => row.OrderId > 0)
            .GroupBy(row =>
                row.EventId is { } id && id != Guid.Empty
                    ? $"event:{id:N}"
                    : $"order:{row.OrderId.ToString(CultureInfo.InvariantCulture)}")
            .Select(group => redactor.RedactTerminal(group.OrderByDescending(row => row.OrderId).First()))
            .Where(row => !string.IsNullOrWhiteSpace(row.Message)).ToArray();
        var groups = rows.GroupBy(row => (row.Message, row.Severity, row.Stream, row.TerminalChannel))
            .Select(group => (
                Row: group.OrderByDescending(row => row.TimestampUtc).ThenByDescending(row => row.OrderId).First(),
                Count: group.Count(), First: group.Min(row => row.TimestampUtc),
                Last: group.Max(row => row.TimestampUtc)))
            .OrderByDescending(group => kind == AssessmentKind.FailureDiagnosis ? Severity(group.Row) : 0)
            .ThenByDescending(group => kind == AssessmentKind.FailureDiagnosis && Relevant(group.Row.Message))
            .ThenByDescending(group => group.Row.TimestampUtc).ThenByDescending(group => group.Row.OrderId).ToArray();
        if (kind != AssessmentKind.FailureDiagnosis)
            // Give each selected channel one representative before allocating the remaining budget by recency.
            groups = groups.GroupBy(g => g.Row.TerminalChannel).Select(g => g.First())
                .Concat(groups.Where(g => g.Row.Severity is DeploymentDiagnosticSeverity.Warning
                        or DeploymentDiagnosticSeverity.Error or DeploymentDiagnosticSeverity.Critical)
                    .GroupBy(g => g.Row.TerminalChannel).Select(g => g.First()))
                .Concat(groups).Distinct().ToArray();
        var selected = new List<Line>();
        var signals = new List<MetricSignal>();
        var traces = new List<TraceSignal>();
        var allMetricGroups = metrics.Take(20000).Where(SafeMetric)
            .GroupBy(point => (point.Container, point.Name, point.Unit)).ToArray();
        var metricGroups = allMetricGroups.Take(24).ToArray();
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
                schemaVersion = 2,
                deployment = SafeMetadata(metadata),
                coverage = new
                {
                    candidateEvents = rows.Length,
                    selectedGroups = selected.Count,
                    duplicateEvents = rows.Length - groups.Length,
                    omittedGroups = groups.Length - selected.Count,
                    earlierOmitted = history.EarlierOmitted || history.Events.Count > MaximumCandidates,
                    historyUnavailable = history.Availability is not null,
                    metricsUnavailable,
                    metricGroupsOmitted = allMetricGroups.Length - signals.Count,
                    metricCandidatesTruncated = metrics.Count > 20000,
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
                message, group.Count, group.First, group.Last,
                "channel-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(row.TerminalChannel)))[..12]);
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
            var containerId = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(group.Key.Container)))[..12];
            signals.Add(new MetricSignal($"metric:{group.Key.Name}:{containerId}:{first.UtcTicks}:{last.UtcTicks}",
                group.Key.Name,
                group.Key.Unit,
                first, last, points.Length, points.Min(point => point.Minimum), points.Max(point => point.Maximum),
                "container-" + containerId, points.Average(point => point.Average),
                points.Length > 1
                    ? points.OrderBy(point => point.Timestamp).Last().Average -
                      points.OrderBy(point => point.Timestamp).First().Average
                    : null));
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

    /// <summary>Bounds configuration metadata and masks it without truncating a JSON document.</summary>
    private JsonElement? SafeMetadata(object? metadata)
    {
        if (metadata is null) return null;
        var json = JsonSerializer.Serialize(metadata);
        if (json.Length > 4096) return JsonSerializer.SerializeToElement(new { configurationOmitted = true });
        using var document = JsonDocument.Parse(json);
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            WriteMetadata(writer, document.RootElement);
        }

        return JsonSerializer.Deserialize<JsonElement>(buffer.ToArray());
    }

    /// <summary>Masks values before JSON encoding so header-like prose cannot corrupt the surrounding document.</summary>
    private void WriteMetadata(Utf8JsonWriter writer, JsonElement value)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (var property in value.EnumerateObject())
                {
                    writer.WritePropertyName(property.Name);
                    WriteMetadata(writer, property.Value);
                }

                writer.WriteEndObject();
                break;
            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var item in value.EnumerateArray()) WriteMetadata(writer, item);
                writer.WriteEndArray();
                break;
            case JsonValueKind.String:
                writer.WriteStringValue(redactor.RedactText(value.GetString()!, 256));
                break;
            default: value.WriteTo(writer); break;
        }
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
        DateTimeOffset? lastTimestamp,
        string channel);

    /// <summary>Range over returned aggregate points; does not fabricate individual samples or causal conclusions.</summary>
    private sealed record MetricSignal(
        string reference,
        string name,
        string unit,
        DateTimeOffset firstTimestamp,
        DateTimeOffset lastTimestamp,
        int returnedPoints,
        double minimum,
        double maximum,
        string container,
        double average,
        double? changeAcrossReturnedIntervals);

    /// <summary>Correlation observed in selected diagnostics, not a fetched span or error-status claim.</summary>
    private sealed record TraceSignal(string reference, int selectedEvents);
}