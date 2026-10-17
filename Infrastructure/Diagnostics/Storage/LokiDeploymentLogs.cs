using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Application.Abstractions.Ai.Context;
using Application.Abstractions.Diagnostics.History;
using Application.Abstractions.Diagnostics.Metrics;
using Application.Abstractions.Diagnostics.Redaction;
using Application.Ai.Context;
using Infrastructure.Diagnostics.Delivery;
using Infrastructure.Diagnostics.Policy;
using Microsoft.Extensions.Options;

namespace Infrastructure.Diagnostics.Storage;

/// <summary>
///     Loki JSON ingestion and bounded structured-metadata queries.
/// </summary>
/// <param name="transport">Bounded authenticated HTTP transport for the private telemetry backends.</param>
/// <param name="options">Provides the configured limits and endpoint settings for this service.</param>
/// <param name="redactor">Applies shared credential and unsafe-text masking.</param>
public sealed class LokiDeploymentLogs(
    TelemetryHttpTransport transport,
    IOptions<TelemetryStorageOptions> options,
    IDiagnosticRedactor redactor)
    : IDeploymentLogWriter, IDeploymentLogQuery, IDeploymentLogSearch, IDeploymentErrorCountQuery
{
    /// <summary>
    ///     Counts recorded deployment error observations in the selected window.
    /// </summary>
    /// <param name="tenant">The internal owner-account telemetry scope.</param>
    /// <param name="project">The project identity used to scope the operation.</param>
    /// <param name="deployment">The exact deployment identity used to scope the operation.</param>
    /// <param name="start">The lower bound of the selected observation window.</param>
    /// <param name="end">The upper bound of the selected observation window.</param>
    /// <param name="token">Cancels the operation when supported by the underlying work.</param>
    /// <returns>A task returning the number of observed error records in the selected window.</returns>
    /// <exception cref="InvalidOperationException">Log error count query failed.</exception>
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

    /// <summary>
    ///     Backend filtering occurs before its result limit; unsupported older adapters fail
    ///     explicitly.
    /// </summary>
    /// <param name="request">The frozen resource scope, selection and bounded read options.</param>
    /// <param name="token">Cancels the operation when supported by the underlying work.</param>
    /// <returns>A task returning the selected bounded redacted log envelopes.</returns>
    public async Task<IReadOnlyList<DeploymentLogEnvelope>> ReadAssessmentAsync(ArchiveAssessmentQuery request,
        CancellationToken token)
    {
        var selection = request.Selection.Normalize();
        if (selection.Sources == AssessmentSources.None) return [];
        var clauses = new List<string>();
        var containers = selection.LogContainers is null
            ? ""
            : " and channel=~" + JsonSerializer.Serialize(selection.LogContainers.Length == 0
                ? "a^"
                : string.Join("|", selection.LogContainers.Select(Regex.Escape)));
        if (selection.Sources.HasFlag(AssessmentSources.Build))
            clauses.Add("(source=\"DockerCompose\" or (source=\"DockerContainer\" and ai_component=\"3\"))");
        if (selection.Sources.HasFlag(AssessmentSources.Web))
            clauses.Add(
                "(((source=\"DockerContainer\" and ai_component=\"4\") or (source=\"AzureContainerApps\" and ai_stream=~\"1|2\"))" +
                containers + ")");
        if (selection.Sources.HasFlag(AssessmentSources.Database))
            clauses.Add("(source=\"DockerContainer\" and ai_component=\"5\"" + containers + ")");
        if (selection.Sources.HasFlag(AssessmentSources.GitHub)) clauses.Add("(source=\"GitHubActions\")");
        if (selection.Sources.HasFlag(AssessmentSources.Azure))
            clauses.Add("(source=\"AzureContainerApps\" and ai_stream=~\"0|3\")");
        if (selection.Sources.HasFlag(AssessmentSources.Deployment)) clauses.Add("(source=~\"AutoMate|DockerDaemon\")");
        if (selection.Sources.HasFlag(AssessmentSources.Other))
            clauses.Add(
                "((source=\"DockerContainer\" and ai_component!~\"3|4|5\") or (source=\"AzureContainerApps\" and ai_stream!~\"0|1|2|3\"))");
        var filter = $" | project_id=\"{request.Project:N}\" | deployment_id=\"{request.Deployment:N}\"" +
                     " | json ai_component=\"event.sourceIdentity.component\", ai_stream=\"event.sourceIdentity.stream\", ai_kind=\"event.kind\", ai_channel=\"channel\"" +
                     " | ai_kind!=\"6\" | ai_channel!=\"\" | (" + string.Join(" or ", clauses) + ")";
        var entries = await QueryAsync(request.Tenant, filter, true, Math.Clamp(request.Limit + 1, 1, 1001), token,
            request.Window.Start, request.Window.End);
        return entries.Where(e => AssessmentClassification.Matches(e, selection, request.Window)).ToArray();
    }

    /// <summary>
    ///     Reads one cursor page in forward or reverse order within the retention window.
    /// </summary>
    /// <param name="tenantId">The internal owner-account telemetry identity.</param>
    /// <param name="projectId">The project identity used for authorization or correlation.</param>
    /// <param name="deploymentId">The exact deployment identity used for ownership or correlation.</param>
    /// <param name="cursor">The previous read position; an absent cursor selects the initial page.</param>
    /// <param name="backwards">Whether to read earlier records rather than records after the cursor.</param>
    /// <param name="limit">The maximum number of returned or processed records.</param>
    /// <param name="cancellationToken">Cancels the operation when supported by the underlying work.</param>
    /// <param name="start">The lower bound of the selected observation window.</param>
    /// <param name="channel">Optional terminal channel, filtered before applying the page limit.</param>
    /// <returns>A task returning a bounded ordered page of redacted log envelopes.</returns>
    public async Task<IReadOnlyList<DeploymentLogEnvelope>> ReadAsync(Guid tenantId, Guid projectId,
        Guid deploymentId, long cursor, bool backwards, int limit, CancellationToken cancellationToken,
        DateTimeOffset? start = null, string? channel = null)
    {
        var filter = $" | project_id=\"{projectId:N}\" | deployment_id=\"{deploymentId:N}\"";
        if (!string.IsNullOrEmpty(channel)) filter += " | channel=" + JsonSerializer.Serialize(channel);
        if (cursor > 0) filter += $" | order_id {(backwards ? "<" : ">")} {cursor}";
        return await QueryAsync(tenantId, filter, backwards, Math.Clamp(limit, 1, 2001), cancellationToken, start);
    }

    /// <summary>
    ///     Confirms exact event identities are queryable before removing their durable buffer.
    /// </summary>
    /// <param name="events">The bounded batch of redacted diagnostic envelopes.</param>
    /// <param name="cancellationToken">Cancels the operation when supported by the underlying work.</param>
    /// <returns>A task returning whether every event identity is queryable in the specialized store.</returns>
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

    /// <summary>
    ///     Reads a bounded ordered log page matching plain-text search within one resource scope.
    /// </summary>
    /// <param name="tenant">The internal owner-account telemetry scope.</param>
    /// <param name="project">The project identity used to scope the operation.</param>
    /// <param name="deployment">The exact deployment identity used to scope the operation.</param>
    /// <param name="cursor">The previous read position; an absent cursor selects the initial page.</param>
    /// <param name="backwards">Whether to read earlier records rather than records after the cursor.</param>
    /// <param name="limit">The maximum number of returned or processed records.</param>
    /// <param name="search">The optional plain-text search term.</param>
    /// <param name="token">Cancels the operation when supported by the underlying work.</param>
    /// <param name="channel">Optional terminal channel, filtered before applying the page limit.</param>
    /// <returns>A task returning a bounded resource-scoped page of matching log envelopes.</returns>
    /// <exception cref="ArgumentException">Log search is limited to 256 characters.</exception>
    public Task<IReadOnlyList<DeploymentLogEnvelope>> SearchAsync(Guid tenant, Guid project, Guid deployment,
        long cursor, bool backwards, int limit, string search, CancellationToken token, string? channel = null)
    {
        if (search.Length > 256) throw new ArgumentException("Log search is limited to 256 characters.");
        var filter = $" | project_id=\"{project:N}\" | deployment_id=\"{deployment:N}\"";
        if (!string.IsNullOrEmpty(channel)) filter += " | channel=" + JsonSerializer.Serialize(channel);
        if (cursor > 0) filter += $" | order_id {(backwards ? "<" : ">")} {cursor}";
        filter += " |= " + JsonSerializer.Serialize(search);
        return QueryAsync(tenant, filter, backwards, Math.Clamp(limit, 1, 2001), token);
    }

    /// <summary>
    ///     Accepts an at-least-once batch; event identities remain unchanged on retry.
    /// </summary>
    /// <param name="events">The bounded batch of redacted diagnostic envelopes.</param>
    /// <param name="cancellationToken">Cancels the operation when supported by the underlying work.</param>
    /// <returns>A task that completes after the redacted batch is written.</returns>
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

    /// <summary>
    ///     Bounds time and results, and rejects unsuccessful query responses.
    /// </summary>
    /// <param name="tenantId">The internal owner-account telemetry identity.</param>
    /// <param name="filter">Bounded provider query filter or search selection.</param>
    /// <param name="backwards">Whether to read earlier records rather than records after the cursor.</param>
    /// <param name="limit">The maximum number of returned or processed records.</param>
    /// <param name="cancellationToken">Cancels the underlying operation when observed.</param>
    /// <param name="start">Absolute observation-window start or owner-authorized cloud launch request.</param>
    /// <param name="end">The upper bound of the selected observation window.</param>
    /// <returns>A task returning the query async selection with its documented bounds and ordering.</returns>
    /// <exception cref="InvalidOperationException">Telemetry log query failed.</exception>
    private async Task<IReadOnlyList<DeploymentLogEnvelope>> QueryAsync(Guid tenantId, string filter,
        bool backwards, int limit, CancellationToken cancellationToken, DateTimeOffset? start = null,
        DateTimeOffset? end = null)
    {
        var now = DateTimeOffset.UtcNow;
        var earliest = start.HasValue && start > now.AddDays(-30) ? start.Value : now.AddDays(-30);
        var until = end is { } upper && upper < now ? upper : now;
        if (until <= earliest) return [];
        var query = "{service_name=\"automate\"}" + filter + $" | expires_at > {now.ToUnixTimeSeconds()}";
        using var response = await transport.SendAsync(Url("loki/api/v1/query_range") +
                                                       $"?query={Uri.EscapeDataString(query)}&start={Nanoseconds(earliest)}&end={Nanoseconds(until)}" +
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

    /// <summary>
    ///     Preserves optional reverse-proxy base paths.
    /// </summary>
    /// <param name="path">Filesystem or provider entry path used by this operation.</param>
    /// <returns>The url derived from the validated input.</returns>
    private string Url(string path)
    {
        return options.Value.LokiUrl.TrimEnd('/') + "/" + path;
    }

    /// <summary>
    ///     Formats UTC ingestion time as Loki nanoseconds without losing precision.
    /// </summary>
    /// <param name="time">The observation timestamp to assign to a UTC bucket.</param>
    /// <returns>The nanoseconds derived from the validated input.</returns>
    private static string Nanoseconds(DateTimeOffset time)
    {
        return ((time.UtcTicks - DateTimeOffset.UnixEpoch.Ticks) * 100).ToString(CultureInfo.InvariantCulture);
    }
}
