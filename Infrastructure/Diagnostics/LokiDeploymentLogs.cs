using System.Globalization;
using System.Text.Json;
using Application.Abstractions.Diagnostics;
using Microsoft.Extensions.Options;

namespace Infrastructure.Diagnostics;

/// <summary>Loki JSON ingestion and bounded structured-metadata queries.</summary>
public sealed class LokiDeploymentLogs(
    TelemetryHttpTransport transport,
    IOptions<TelemetryStorageOptions> options,
    IDiagnosticRedactor redactor)
    : IDeploymentLogWriter, IDeploymentLogQuery, IDeploymentLogSearch, IDeploymentErrorCountQuery
{
    public async Task<long> CountErrorsAsync(Guid tenant, Guid project, Guid deployment, DateTimeOffset start,
        DateTimeOffset end, CancellationToken token)
    {
        var seconds = Math.Max(1, (int)Math.Ceiling((end - start).TotalSeconds));
        var query = $"sum(count_over_time({{service_name=\"automate\",severity=~\"Error|Critical\"}}" +
                    $" | project_id=\"{project:N}\" | deployment_id=\"{deployment:N}\" [{seconds}s]))";
        using var response = await transport.SendAsync(Url("loki/api/v1/query") +
                                                       $"?query={Uri.EscapeDataString(query)}&time={Nanoseconds(end)}",
            tenant, null, token);
        if (response.RootElement.GetProperty("status").GetString() != "success")
            throw new InvalidOperationException("Log error count query failed.");
        return response.RootElement.GetProperty("data").GetProperty("result").EnumerateArray().Sum(item =>
            double.TryParse(item.GetProperty("value")[1].GetString(), CultureInfo.InvariantCulture, out var value) &&
            double.IsFinite(value) && value >= 0
                ? (long)value
                : 0);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<DeploymentLogEnvelope>> ReadAsync(Guid tenantId, Guid projectId,
        Guid deploymentId, long cursor, bool backwards, int limit, CancellationToken cancellationToken,
        DateTimeOffset? start = null)
    {
        var filter = $" | project_id=\"{projectId:N}\" | deployment_id=\"{deploymentId:N}\"";
        if (cursor > 0) filter += $" | order_id {(backwards ? "<" : ">")} {cursor}";
        return await QueryAsync(tenantId, filter, backwards, Math.Clamp(limit, 1, 2001), cancellationToken, start);
    }

    /// <inheritdoc />
    public async Task<bool> ContainsAsync(IReadOnlyList<DeploymentLogEnvelope> events,
        CancellationToken cancellationToken)
    {
        if (events.Count == 0) return true;
        var filter = " | event_id=~\"" + string.Join('|', events.Select(e => e.EventId.ToString("N"))) + "\"";
        var visible = await QueryAsync(events[0].TenantId, filter, false, events.Count, cancellationToken,
            events.Min(e => e.StoredAt).AddSeconds(-1));
        var ids = visible.Select(e => e.EventId).ToHashSet();
        return events.All(e => ids.Contains(e.EventId));
    }

    public Task<IReadOnlyList<DeploymentLogEnvelope>> SearchAsync(Guid tenant, Guid project, Guid deployment,
        long cursor, bool backwards, int limit, string search, CancellationToken token)
    {
        if (search.Length > 256) throw new ArgumentException("Log search is limited to 256 characters.");
        var filter = $" | project_id=\"{project:N}\" | deployment_id=\"{deployment:N}\"";
        if (cursor > 0) filter += $" | order_id {(backwards ? "<" : ">")} {cursor}";
        filter += " |= " + JsonSerializer.Serialize(search);
        return QueryAsync(tenant, filter, backwards, Math.Clamp(limit, 1, 2001), token);
    }

    /// <inheritdoc />
    public async Task WriteAsync(IReadOnlyList<DeploymentLogEnvelope> events, CancellationToken cancellationToken)
    {
        events = TelemetryWriteBoundary.Snapshot(events, redactor, cancellationToken);
        if (events.Count == 0) return;
        var streams = events.GroupBy(e => new { e.Event.Source, e.Event.Severity }).Select(group => new
        {
            stream = new
            {
                service_name = "automate",
                source = group.Key.Source.ToString(),
                severity = group.Key.Severity.ToString()
            },
            values = group.OrderBy(e => e.StoredAt).Select(e => new object[]
            {
                Nanoseconds(e.StoredAt), JsonSerializer.Serialize(e, TelemetryHttpTransport.Json),
                new Dictionary<string, string>
                {
                    ["project_id"] = e.Event.ProjectId.ToString("N"),
                    ["deployment_id"] = e.Event.DeploymentId?.ToString("N") ?? "none",
                    ["event_id"] = e.EventId.ToString("N"),
                    ["order_id"] = e.OrderId.ToString(CultureInfo.InvariantCulture),
                    ["channel"] = e.Channel ?? "system",
                    ["expires_at"] = e.ExpiresAt.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture)
                }
            }).ToArray()
        }).ToArray();
        using var response = await transport.SendAsync(Url("loki/api/v1/push"), events[0].TenantId,
            new { streams }, cancellationToken);
    }

    /// <summary>Bounds time and results, and rejects unsuccessful query responses.</summary>
    private async Task<IReadOnlyList<DeploymentLogEnvelope>> QueryAsync(Guid tenantId, string filter,
        bool backwards, int limit, CancellationToken cancellationToken, DateTimeOffset? start = null)
    {
        var now = DateTimeOffset.UtcNow;
        var earliest = start.HasValue && start > now.AddDays(-30) ? start.Value : now.AddDays(-30);
        var query = "{service_name=\"automate\"}" + filter + $" | expires_at > {now.ToUnixTimeSeconds()}";
        using var response = await transport.SendAsync(Url("loki/api/v1/query_range") +
                                                       $"?query={Uri.EscapeDataString(query)}&start={Nanoseconds(earliest)}&end={Nanoseconds(now)}" +
                                                       $"&direction={(backwards ? "backward" : "forward")}&limit={limit}",
            tenantId, null, cancellationToken);
        if (response.RootElement.GetProperty("status").GetString() != "success")
            throw new InvalidOperationException("Telemetry log query failed.");
        var result = new List<DeploymentLogEnvelope>();
        foreach (var stream in response.RootElement.GetProperty("data").GetProperty("result").EnumerateArray())
        foreach (var value in stream.GetProperty("values").EnumerateArray())
        {
            var envelope =
                JsonSerializer.Deserialize<DeploymentLogEnvelope>(value[1].GetString()!, TelemetryHttpTransport.Json);
            if (envelope is not null && envelope.TenantId == tenantId && envelope.ExpiresAt > now)
                result.Add(envelope with
                {
                    Event = redactor.Redact(envelope.Event).Event,
                    Channel = envelope.Channel is null ? null : redactor.RedactText(envelope.Channel, 128)
                });
        }

        return result.DistinctBy(e => e.EventId).OrderBy(e => e.OrderId).ToArray();
    }

    /// <summary>Preserves optional reverse-proxy base paths.</summary>
    private string Url(string path)
    {
        return options.Value.LokiUrl.TrimEnd('/') + "/" + path;
    }

    /// <summary>Formats UTC ingestion time as Loki nanoseconds without losing precision.</summary>
    private static string Nanoseconds(DateTimeOffset time)
    {
        return ((time.UtcTicks - DateTimeOffset.UnixEpoch.Ticks) * 100).ToString(CultureInfo.InvariantCulture);
    }
}