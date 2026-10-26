using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Application.Abstractions.Ai.Context;
using Application.Abstractions.Diagnostics.Events;
using Application.Abstractions.Diagnostics.Metrics;
using Application.Abstractions.Diagnostics.Redaction;

namespace Application.Ai.Context;

/// <summary>
///     Selects bounded diagnostic evidence using assessment focus, source representation,
///     severity and recency.
/// </summary>
/// <param name="redactor">The shared redaction boundary used before persistence or presentation.</param>
public sealed class AnalysisContextSelector(IDiagnosticRedactor redactor)
{
    /// <summary>Caps CPU/memory work independently of provider text limits.</summary>
    public const int MaximumCandidates = 1000;

    /// <summary>Preserves camel-case evidence fields independently of C# member naming.</summary>
    private static readonly JsonSerializerOptions ContextJson = new()
        { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    /// <summary>Failure terms used to rank diagnostic evidence.</summary>
    private static readonly string[] SourceArray = ["error", "fail", "exception", "timeout", "denied", "unable"];

    /// <summary>
    ///     Serializes a bounded structured data document with stable IDs and explicit partial
    ///     coverage.
    /// </summary>
    /// <param name="history">The bounded terminal history and its coverage metadata.</param>
    /// <param name="metrics">Recorded numeric samples or aggregate points.</param>
    /// <param name="budget">The character, UTF-8 byte and conservative token limits.</param>
    /// <param name="metricsUnavailable">Whether metric collection reported an availability failure.</param>
    /// <param name="kind">The assessment focus requested or resolved for the deployment.</param>
    /// <param name="metadata">
    ///     The non-secret project metadata supplied to evidence or artifact generation.
    /// </param>
    /// <param name="token">Cancels the operation when supported by the underlying work.</param>
    /// <returns>Bounded redacted context with an exact permitted-evidence reference catalog.</returns>
    /// <exception cref="OperationCanceledException">Evidence selection is canceled.</exception>
    public DeploymentAnalysisContext Select(DeploymentTerminalHistory history,
        IReadOnlyList<DeploymentMetricPoint> metrics,
        AnalysisContextBudget budget, bool metricsUnavailable,
        AssessmentKind kind = AssessmentKind.FailureDiagnosis,
        object? metadata = null, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();

        // Select the most recent events with the highest order IDs.
        var rows = history.Events.Take(MaximumCandidates).Where(row => row.OrderId > 0)
            .GroupBy(row =>
                row.EventId is { } id && id != Guid.Empty
                    ? $"event:{id:N}"
                    : $"order:{row.OrderId.ToString(CultureInfo.InvariantCulture)}")
            .Select(group => redactor
                .RedactTerminal(group.OrderByDescending(row => row.OrderId).First()))
            .Where(row => !string.IsNullOrWhiteSpace(row.Message)).ToArray();

        // Group by message, severity, stream and channel to avoid repeated evidence; keep the most recent representative.
        var groups = rows.GroupBy(row => (row.Message, row.Severity, row.Stream, row.TerminalChannel))
            .Select(group => (
                Row: group.OrderByDescending(row => row.TimestampUtc)
                    .ThenByDescending(row => row.OrderId).First(),
                Count: group.Count(),
                First: group.Min(row => row.TimestampUtc),
                Last: group.Max(row => row.TimestampUtc)))
            .OrderByDescending(group => kind == AssessmentKind.FailureDiagnosis ? Severity(group.Row) : 0)
            .ThenByDescending(group => kind == AssessmentKind.FailureDiagnosis && Relevant(group.Row.Message))
            .ThenByDescending(group => group.Row.TimestampUtc)
            .ThenByDescending(group => group.Row.OrderId).ToArray();

        if (kind != AssessmentKind.FailureDiagnosis)
            // Give each selected channel one representative before allocating the remaining budget by recency.
            groups =
            [
                .. groups.GroupBy(g => g.Row.TerminalChannel).Select(g => g.First())
                    .Concat(groups.Where(g => g.Row.Severity is DeploymentDiagnosticSeverity.Warning
                            or DeploymentDiagnosticSeverity.Error or DeploymentDiagnosticSeverity.Critical)
                        .GroupBy(g => g.Row.TerminalChannel).Select(g => g.First()))
                    .Concat(groups).Distinct()
            ];

        var selected = new List<Line>();
        var signals = new List<MetricSignal>();
        var traces = new List<TraceSignal>();

        // Limit the number of metric groups to avoid excessive CPU/memory work and ensure a stable JSON document.
        var allMetricGroups = metrics.Take(20000).Where(SafeMetric)
            .GroupBy(point => (point.Container, point.Name, point.Unit)).ToArray();

        var metricGroups = allMetricGroups.Take(24).ToArray();

        // Count the number of unique valid traces in the original rows to report coverage.
        var availableTraces = rows
            .Where(row => ValidTrace(row.TraceId))
            .Select(row => row.TraceId)
            .Distinct()
            .Count();

        // Reduce the budget for logs when metrics or traces are present, but never below 1 character/byte/token.
        var logBudget = metricGroups.Length > 0 || availableTraces > 0
            ? new AnalysisContextBudget(
                Math.Max(1, budget.Characters * 3 / 4),
                Math.Max(1, budget.Bytes * 3 / 4),
                Math.Max(1, budget.Tokens * 3 / 4))
            : budget;

        foreach (var group in groups)
        {
            token.ThrowIfCancellationRequested();

            var row = group.Row;
            var message = redactor.RedactText(row.Message, 1024);

            var line = new Line(
                Reference(row), row.OrderId, row.TimestampUtc, row.Sequence, row.Severity
                    is { } severity && Enum.IsDefined(severity)
                    ? Enum.GetName(severity)!
                    : "Unknown",
                message, group.Count, group.First, group.Last,
                "channel-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(row.TerminalChannel)))[..12]);

            selected.Add(line);

            // Shorten only message data, keeping the evidence identity and coverage document intact.
            while (!logBudget.Fits(Render()) && message.Length > 64)
            {
                message = redactor.RedactText(row.Message, message.Length / 2);
                selected[^1] = line with { Message = message };
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
                first, last, points.Length,
                points.Min(point => point.Minimum), points.Max(point => point.Maximum),
                "container-" + containerId, points.Average(point => point.Average),
                points.Length > 1
                    ? points.OrderBy(point => point.Timestamp).Last().Average -
                      points.OrderBy(point => point.Timestamp).First().Average
                    : null));

            if (!budget.Fits(Render())) signals.RemoveAt(signals.Count - 1);
        }

        var selectedIds = selected.Select(line => line.Reference).ToHashSet(StringComparer.Ordinal);

        // Filter and group the rows by trace ID, taking the first 20 groups.
        foreach (var group in rows
                     .Where(row => selectedIds.Contains(Reference(row)) && ValidTrace(row.TraceId))
                     .GroupBy(row => row.TraceId).Take(20))
        {
            traces.Add(new TraceSignal($"trace:{group.Key}", group.Count()));
            if (!budget.Fits(Render())) traces.RemoveAt(traces.Count - 1);
        }

        if (selected.Count == 0 && signals.Count == 0) return new DeploymentAnalysisContext("", []);

        var text = Render();

        // Return the bounded context with stable evidence references, even if the document is truncated.
        return new DeploymentAnalysisContext(text, Array.AsReadOnly([
            .. selected.Select(line => line.Reference),
            .. signals.Select(signal => signal.Reference),
            .. traces.Select(trace => trace.Reference)
        ]));

        /*
         * Render the bounded context as a JSON document with stable IDs and explicit coverage.
         * The document is truncated to fit the budget, but the evidence references remain intact.
         */
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
                records = selected.OrderBy(line => line.Timestamp).ThenBy(line => line.Order).ToArray(),
                metricSignals = signals,
                traceSignals = traces
            }, ContextJson);
        }
    }

    /// <summary>
    ///     Uses only stable durable event/order identities, never a provider cursor or user
    ///     supplied label.
    /// </summary>
    /// <param name="row">The terminal observation being normalized or ranked.</param>
    /// <returns>The event identity or durable ordering reference used to ground the observation.</returns>
    private static string Reference(DeploymentTerminalLog row)
    {
        return row.EventId is { } id && id != Guid.Empty
            ? $"event:{id:N}"
            : $"order:{row.OrderId.ToString(CultureInfo.InvariantCulture)}";
    }

    /// <summary>
    ///     Ranks defined severities; stderr without severity ranks as a warning.
    /// </summary>
    /// <param name="row">The terminal observation being normalized or ranked.</param>
    /// <returns>The finite severity rank used to prioritize evidence.</returns>
    private static int Severity(DeploymentTerminalLog row)
    {
        return row.Severity is { } severity && Enum.IsDefined(severity)
            ? (int)severity
            : row.Stream == DeploymentDiagnosticStream.StandardError
                ? 3
                : 2;
    }

    /// <summary>
    ///     Simple bounded relevance heuristic; log content never becomes instructions.
    /// </summary>
    /// <param name="text">The diagnostic context or text being processed.</param>
    /// <returns>Whether the text contains a reviewed operational-failure keyword.</returns>
    private static bool Relevant(string text)
    {
        return SourceArray.Any(word => text.Contains(word, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    ///     Only finite, nonnegative known numeric series may enter the summarized context.
    /// </summary>
    /// <param name="point">The numeric aggregate to validate before adding it to context.</param>
    /// <returns>Whether the point has a supported metric/unit pair and finite nonnegative values.</returns>
    private static bool SafeMetric(DeploymentMetricPoint point)
    {
        return (point.Name, point.Unit) is
               ("automate_cpu_usage_cores", "cores") or ("automate_memory_used_bytes", "bytes")
               or ("automate_memory_limit_bytes", "bytes") &&
               double.IsFinite(point.Minimum) && double.IsFinite(point.Maximum) && double.IsFinite(point.Average) &&
               point.Minimum >= 0 && point.Minimum <= point.Average && point.Average <= point.Maximum;
    }

    /// <summary>
    ///     Accepts canonical nonzero trace IDs without arbitrary provider metadata.
    /// </summary>
    /// <param name="trace">The recorded hexadecimal trace identity, if known.</param>
    /// <returns>Whether the supplied value is a canonical nonzero hexadecimal trace identity.</returns>
    private static bool ValidTrace(string? trace)
    {
        return trace is { Length: 32 } && trace.Any(character => character != '0') && trace.All(Uri.IsHexDigit);
    }

    /// <summary>
    ///     Bounds configuration metadata and masks it without truncating a JSON document.
    /// </summary>
    /// <param name="metadata">
    ///     The non-secret project metadata supplied to evidence or artifact generation.
    /// </param>
    /// <returns>
    ///     Redacted JSON metadata, an omission marker when oversized, or null when no metadata was
    ///     supplied.
    /// </returns>
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

    /// <summary>
    ///     Masks values before JSON encoding so header-like prose cannot corrupt the surrounding
    ///     document.
    /// </summary>
    /// <param name="writer">The JSON writer receiving normalized context data.</param>
    /// <param name="value">The value to normalize, redact or validate.</param>
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

            case JsonValueKind.Undefined:
            case JsonValueKind.Number:
            case JsonValueKind.True:
            case JsonValueKind.False:
            case JsonValueKind.Null:

            default: value.WriteTo(writer); break;
        }
    }

    /// <summary>
    ///     Bounded evidence group retaining the representative actual event and window timestamps.
    /// </summary>
    /// <param name="Reference">The exact evidence identifier being validated.</param>
    /// <param name="Order">The durable log position encoded in the cursor.</param>
    /// <param name="Timestamp">The provider observation timestamp, if known.</param>
    /// <param name="Sequence">The optional provider-local sequence number.</param>
    /// <param name="Severity">The normalized severity used to rank a context group.</param>
    /// <param name="Message">The message supplied to the operation.</param>
    /// <param name="Occurrences">The number of repeated observations represented by the group.</param>
    /// <param name="FirstTimestamp">The first observation time in the returned selection, if known.</param>
    /// <param name="LastTimestamp">The last observation time in the returned selection, if known.</param>
    /// <param name="Channel">The stable terminal channel used for routing.</param>
    private sealed record Line(
        string Reference,
        long Order,
        DateTimeOffset? Timestamp,
        long? Sequence,
        string Severity,
        string Message,
        int Occurrences,
        DateTimeOffset? FirstTimestamp,
        DateTimeOffset? LastTimestamp,
        string Channel);

    /// <summary>
    ///     Range over returned aggregate points; does not fabricate individual samples or causal
    ///     conclusions.
    /// </summary>
    /// <param name="Reference">The exact evidence identifier being validated.</param>
    /// <param name="Name">The recorded display name or fixed metric identity.</param>
    /// <param name="Unit">The native metric unit associated with the sample.</param>
    /// <param name="FirstTimestamp">The first observation time in the returned selection, if known.</param>
    /// <param name="LastTimestamp">The last observation time in the returned selection, if known.</param>
    /// <param name="ReturnedPoints">The number of points included in the bounded selection.</param>
    /// <param name="Minimum">The observed minimum in the recorded native unit.</param>
    /// <param name="Maximum">The observed maximum in the recorded native unit.</param>
    /// <param name="Container">
    ///     The recorded container identity; null selects all containers where supported.
    /// </param>
    /// <param name="Average">The observed arithmetic mean; absent values remain unknown.</param>
    /// <param name="ChangeAcrossReturnedIntervals">
    ///     The observed change across returned intervals, if known.
    /// </param>
    private sealed record MetricSignal(
        string Reference,
        string Name,
        string Unit,
        DateTimeOffset FirstTimestamp,
        DateTimeOffset LastTimestamp,
        int ReturnedPoints,
        double Minimum,
        double Maximum,
        string Container,
        double Average,
        double? ChangeAcrossReturnedIntervals);

    /// <summary>
    ///     Correlation observed in selected diagnostics, not a fetched span or error-status claim.
    /// </summary>
    /// <param name="Reference">The exact evidence identifier being validated.</param>
    /// <param name="SelectedEvents">The number of diagnostic observations included in context.</param>
    private sealed record TraceSignal(string Reference, int SelectedEvents);
}
