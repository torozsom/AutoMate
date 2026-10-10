using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Application.Abstractions.Ai;
using Application.Abstractions.Diagnostics;
using Microsoft.Extensions.Options;

namespace Infrastructure.Diagnostics;

/// <summary>OTLP/HTTP numeric ingestion and Prometheus-compatible historical queries.</summary>
public sealed class MimirDeploymentMetrics(
    TelemetryHttpTransport transport,
    IOptions<TelemetryStorageOptions> options,
    IDiagnosticRedactor redactor)
    : IDeploymentMetricWriter, IDeploymentMetricQuery, IDailyDeploymentMetricQuery
{
    /// <summary>Fixed series names prevent arbitrary metric/label injection.</summary>
    internal static readonly IReadOnlyDictionary<string, string> Units = new Dictionary<string, string>
    {
        ["automate_cpu_usage_cores"] = "cores",
        ["automate_memory_used_bytes"] = "bytes",
        ["automate_memory_limit_bytes"] = "bytes"
    };

    public static IReadOnlyDictionary<string, string> SupportedUnits => Units;

    /// <inheritdoc />
    public async Task<IReadOnlyList<DailyMetricStatistics>> ReadDailyAsync(Guid tenant, Guid project, Guid deployment,
        DateTimeOffset start, DateTimeOffset end, CancellationToken token)
    {
        var seconds = Math.Max(1, (int)Math.Ceiling((end - start).TotalSeconds));
        var result = new List<DailyMetricStatistics>();
        foreach (var name in Units.Keys)
        {
            var series = new Dictionary<string, double[]>();
            var functions = new[] { "count_over_time", "sum_over_time", "min_over_time", "max_over_time" };
            for (var i = 0; i < functions.Length; i++)
            {
                var query = $"{functions[i]}({Selector(name, project, deployment)}[{seconds}s])";
                using var response = await QueryAsync(tenant, "query", query, "&time=" + Seconds(end), token);
                foreach (var item in response.RootElement.GetProperty("data").GetProperty("result").EnumerateArray())
                {
                    var container = item.GetProperty("metric").GetProperty("container").GetString()!;
                    if (!series.TryGetValue(container, out var values))
                        series[container] = values = Enumerable.Repeat(double.NaN, 4).ToArray();
                    if (double.TryParse(item.GetProperty("value")[1].GetString(), CultureInfo.InvariantCulture,
                            out var value) && double.IsFinite(value))
                        values[i] = value;
                }
            }

            result.AddRange(series.Where(p => p.Value.All(double.IsFinite) && p.Value[0] > 0).Select(p =>
                new DailyMetricStatistics(redactor.RedactText(p.Key, 128), name, Units[name], (long)p.Value[0],
                    p.Value[1], p.Value[2],
                    p.Value[3])));
        }

        return result;
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<DeploymentMetricPoint>> ReadAsync(Guid tenantId, Guid projectId, Guid deploymentId,
        DateTimeOffset start, DateTimeOffset end, int maximumPoints, CancellationToken cancellationToken)
    {
        return ReadCoreAsync(tenantId, projectId, deploymentId, start, end, maximumPoints, cancellationToken, null);
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<DeploymentMetricPoint>> ReadAssessmentAsync(ArchiveAssessmentQuery query,
        CancellationToken token)
    {
        return !query.Selection.Normalize().IncludeMetrics
            ? Task.FromResult<IReadOnlyList<DeploymentMetricPoint>>([])
            : ReadCoreAsync(query.Tenant, query.Project, query.Deployment, query.Window.Start, query.Window.End, 100,
                token,
                query.Selection.Normalize().MetricContainers);
    }

    /// <inheritdoc />
    public async Task<bool> ContainsAsync(IReadOnlyList<DeploymentLogEnvelope> events,
        CancellationToken cancellationToken)
    {
        events = TelemetryWriteBoundary.Snapshot(events, redactor, cancellationToken);
        if (events.Count == 0) return true;
        ValidateSamples(events);
        // Range selectors return raw sample timestamps; evaluated gauge timestamps cannot prove visibility.
        foreach (var group in events.SelectMany(e => (e.Event.Metrics ?? []).Select(m => new { e, m }))
                     .GroupBy(x => new { x.e.Event.ProjectId, x.e.Event.DeploymentId, x.m.Name }))
        {
            if (group.Key.DeploymentId is null) return false;
            var query = Selector(group.Key.Name, group.Key.ProjectId, group.Key.DeploymentId.Value) + "[24h]";
            using var response = await QueryAsync(events[0].TenantId, "query", query,
                "&time=" + Seconds(DateTimeOffset.UtcNow), cancellationToken);
            var samples = new HashSet<(string, long, double)>();
            foreach (var series in response.RootElement.GetProperty("data").GetProperty("result").EnumerateArray())
            foreach (var pair in series.GetProperty("values").EnumerateArray())
                if (double.TryParse(pair[1].GetString(), CultureInfo.InvariantCulture, out var value))
                    samples.Add((series.GetProperty("metric").GetProperty("container").GetString()!,
                        (long)Math.Round(pair[0].GetDouble() * 1000), value));
            if (group.Any(x => !samples.Contains((x.e.Event.TerminalChannel.Target!,
                    x.e.Event.TimestampUtc.ToUnixTimeMilliseconds(), x.m.Value)))) return false;
        }

        return true;
    }

    /// <inheritdoc />
    public async Task WriteAsync(IReadOnlyList<DeploymentLogEnvelope> events, CancellationToken cancellationToken)
    {
        events = TelemetryWriteBoundary.Snapshot(events, redactor, cancellationToken);
        if (events.Count == 0) return;
        ValidateSamples(events);
        var metrics = events.SelectMany(e => (e.Event.Metrics ?? []).Select(m => new { Envelope = e, Sample = m }))
            .GroupBy(x => x.Sample.Name).Select(group => new
            {
                name = group.Key,
                gauge = new
                {
                    dataPoints = group.OrderBy(x => x.Envelope.Event.TimestampUtc).Select(x => new
                    {
                        timeUnixNano = ((x.Envelope.Event.TimestampUtc.UtcTicks - DateTimeOffset.UnixEpoch.Ticks) * 100)
                            .ToString(CultureInfo.InvariantCulture),
                        asDouble = x.Sample.Value,
                        attributes = new[]
                        {
                            Attribute("project_id", x.Envelope.Event.ProjectId.ToString("N")),
                            Attribute("deployment_id", x.Envelope.Event.DeploymentId?.ToString("N") ?? "none"),
                            Attribute("container", x.Envelope.Event.TerminalChannel.Target ?? "unknown"),
                            Attribute("source", x.Envelope.Event.Source.ToString())
                        }
                    }).ToArray()
                }
            }).ToArray();
        using var response = await transport.SendAsync(options.Value.MetricsWriteUrl, events[0].TenantId,
            new { resourceMetrics = new[] { new { scopeMetrics = new[] { new { metrics } } } } }, cancellationToken);
        if (response.RootElement.TryGetProperty("partialSuccess", out var partial) &&
            partial.TryGetProperty("rejectedDataPoints", out var rejected) && rejected.ToString() != "0")
            throw new InvalidOperationException("Telemetry metric batch was partially rejected.");
    }

    /// <summary>Filters metric labels before interval aggregation.</summary>
    private async Task<IReadOnlyList<DeploymentMetricPoint>> ReadCoreAsync(Guid tenantId, Guid projectId,
        Guid deploymentId,
        DateTimeOffset start, DateTimeOffset end, int maximumPoints, CancellationToken cancellationToken,
        string[]? selected)
    {
        if (selected is { Length: 0 }) return [];
        maximumPoints = Math.Clamp(maximumPoints, 1, 1000);
        var interval = Math.Max(60, (int)Math.Ceiling((end - start).TotalSeconds / maximumPoints));
        var earliest = DateTimeOffset.UtcNow.AddDays(-30).AddSeconds(interval);
        if (start < earliest) start = earliest;
        if (end <= start) return [];
        start = end.AddSeconds(-Math.Floor((end - start).TotalSeconds / interval) * interval);
        var points = new Dictionary<(string Container, string Name, DateTimeOffset Time), double[]>();
        foreach (var name in Units.Keys)
        {
            var selector = Selector(name, projectId, deploymentId);
            if (selected is not null)
                selector = selector[..^1] + ",container=~" +
                           JsonSerializer.Serialize(string.Join("|", selected.Select(Regex.Escape))) + "}";
            var aggregation = new[] { "avg_over_time", "min_over_time", "max_over_time" };
            for (var i = 0; i < aggregation.Length; i++)
            {
                var query = $"{aggregation[i]}({selector}[{interval}s])";
                using var response = await QueryAsync(tenantId, "query_range", query,
                    $"&start={Seconds(start)}&end={Seconds(end)}&step={interval}", cancellationToken);
                foreach (var series in response.RootElement.GetProperty("data").GetProperty("result").EnumerateArray())
                foreach (var pair in series.GetProperty("values").EnumerateArray())
                {
                    var container = series.GetProperty("metric").GetProperty("container").GetString()!;
                    var time = DateTimeOffset.FromUnixTimeMilliseconds((long)(pair[0].GetDouble() * 1000));
                    if (!double.TryParse(pair[1].GetString(), CultureInfo.InvariantCulture, out var value) ||
                        !double.IsFinite(value)) continue;
                    var key = (container, name, time);
                    if (!points.TryGetValue(key, out var values))
                        points[key] = values = [double.NaN, double.NaN, double.NaN];
                    values[i] = value;
                }
            }
        }

        return points.Where(p => p.Value.All(double.IsFinite)).Select(p => new DeploymentMetricPoint(
                redactor.RedactText(p.Key.Container, 128), p.Key.Name, Units[p.Key.Name], p.Key.Time, p.Value[0],
                p.Value[1], p.Value[2]))
            .OrderBy(p => p.Timestamp).ToArray();
    }

    /// <summary>Rejects unsupported or nonfinite samples before store writes and visibility queries.</summary>
    private static void ValidateSamples(IReadOnlyList<DeploymentLogEnvelope> events)
    {
        if (events.Any(item => item.Event.Metrics?.Any(sample =>
                !double.IsFinite(sample.Value) || sample.Value < 0 ||
                !Units.TryGetValue(sample.Name, out var unit) || unit != sample.Unit) == true))
            throw new ArgumentException("Telemetry metric batch contains unsupported samples.");
    }

    /// <summary>Restricts queries to server-constructed GUID selectors.</summary>
    private static string Selector(string name, Guid project, Guid deployment)
    {
        return $"{name}{{project_id=\"{project:N}\",deployment_id=\"{deployment:N}\"}}";
    }

    /// <summary>Issues a bounded query against an operator-configured base endpoint.</summary>
    private Task<JsonDocument> QueryAsync(Guid tenant, string endpoint, string query, string suffix,
        CancellationToken token)
    {
        return transport.SendAsync(options.Value.MetricsQueryUrl.TrimEnd('/') + "/api/v1/" + endpoint +
                                   "?query=" + Uri.EscapeDataString(query) + suffix, tenant, null, token);
    }

    /// <summary>Creates a string OTLP attribute without resource promotion dependencies.</summary>
    private static object Attribute(string key, string value)
    {
        return new { key, value = new { stringValue = value } };
    }

    /// <summary>Formats query time invariantly.</summary>
    private static string Seconds(DateTimeOffset time)
    {
        return (time.ToUnixTimeMilliseconds() / 1000d).ToString(CultureInfo.InvariantCulture);
    }
}