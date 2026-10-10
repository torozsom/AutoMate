using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Application.Abstractions.Ai;
using Application.Abstractions.Diagnostics;
using Microsoft.Extensions.Options;

namespace Infrastructure.Diagnostics;

/// <summary>Only server-side callers possess the private ingestion credential.</summary>
public sealed class TelemetryGatewayClient(IHttpClientFactory clients, IOptions<TelemetryStorageOptions> options)
    : ITelemetryGateway, IDeploymentArchive
{
    /// <inheritdoc />
    public Task<ArchiveMetricBatch> ReadMetricBatchAsync(ArchiveMetricBatchRequest request, CancellationToken token)
    {
        return SendArchiveAsync<ArchiveMetricBatch>("archive/metric-batch", request, token);
    }

    /// <inheritdoc />
    public Task<ArchiveAssessmentPage> ReadAssessmentAsync(ArchiveAssessmentQuery query, CancellationToken token)
    {
        return SendArchiveAsync<ArchiveAssessmentPage>("archive/assessment", query, token);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<DeploymentMetricPoint>> ReadAssessmentMetricsAsync(ArchiveAssessmentQuery query,
        CancellationToken token)
    {
        return await SendArchiveAsync<DeploymentMetricPoint[]>("archive/assessment-metrics", query, token);
    }

    /// <inheritdoc />
    public Task<DeploymentLogEnvelope> AppendAsync(DeploymentLogEnvelope envelope, CancellationToken token)
    {
        return SendArchiveAsync<DeploymentLogEnvelope>("archive/import", envelope, token);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<DeploymentLogEnvelope>> ReadAsync(Guid tenant, Guid project, Guid deployment,
        long cursor, bool backwards, int limit, string? search, CancellationToken token)
    {
        return await SendArchiveAsync<DeploymentLogEnvelope[]>(
            $"archive/{tenant:N}/{project:N}/{deployment:N}?cursor={cursor}&backwards={backwards.ToString().ToLowerInvariant()}&limit={Math.Clamp(limit, 1, 2001)}&search={Uri.EscapeDataString(search ?? "")}",
            null, token);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<DeploymentMetricPoint>> ReadMetricsAsync(Guid tenant, Guid project, Guid deployment,
        DateTimeOffset start, DateTimeOffset end, int maximumPoints, CancellationToken token)
    {
        return await SendArchiveAsync<DeploymentMetricPoint[]>("archive/metrics",
            new ArchiveMetricRequest(tenant, project, deployment, start, end, Math.Clamp(maximumPoints, 1, 1000)),
            token);
    }

    /// <inheritdoc />
    public async Task DeleteProjectAsync(Guid tenant, Guid project, CancellationToken token)
    {
        await SendArchiveAsync<JsonElement>("archive/delete",
            new ArchiveDeleteRequest(tenant, project), token);
    }

    /// <inheritdoc />
    public async Task ImportMetricsAsync(ArchiveMetricImport import, CancellationToken token)
    {
        await SendArchiveAsync<JsonElement>("archive/import-metrics", import, token);
    }

    public async Task<DeploymentLogEnvelope> AcceptAsync(DeploymentDiagnosticEvent diagnosticEvent, string? channel,
        CancellationToken cancellationToken)
    {
        using var request = Request(HttpMethod.Post, "ingest");
        request.Content = JsonContent.Create(new TelemetryIngestRequest(diagnosticEvent, channel));
        using var response = await clients.CreateClient("DeploymentTelemetry").SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<DeploymentLogEnvelope>(cancellationToken) ??
               throw new InvalidOperationException("Telemetry gateway returned no receipt.");
    }

    public async Task<TelemetryPendingHistory> ReadPendingAsync(Guid tenant, Guid project, Guid deployment,
        CancellationToken cancellationToken)
    {
        using var request = Request(HttpMethod.Get, $"pending/{tenant:N}/{project:N}/{deployment:N}");
        using var response = await clients.CreateClient("DeploymentTelemetry").SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<TelemetryPendingHistory>(cancellationToken) ??
               throw new InvalidOperationException("Telemetry gateway returned no history.");
    }

    /// <summary>Bounds archive response bodies and cancellation across headers and decoding.</summary>
    private async Task<T> SendArchiveAsync<T>(string path, object? payload, CancellationToken token)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(30));
        using var request = Request(payload is null ? HttpMethod.Get : HttpMethod.Post, path);
        if (payload is not null) request.Content = JsonContent.Create(payload);
        using var response = await clients.CreateClient("DeploymentTelemetry").SendAsync(request,
            HttpCompletionOption.ResponseHeadersRead, deadline.Token);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(deadline.Token);
        using var body = new MemoryStream();
        var buffer = new byte[8192];
        int count;
        while ((count = await stream.ReadAsync(buffer, deadline.Token)) > 0)
        {
            if (body.Length + count > 8 * 1024 * 1024)
                throw new InvalidOperationException("Archive response exceeds its bound.");
            await body.WriteAsync(buffer.AsMemory(0, count), deadline.Token);
        }

        return JsonSerializer.Deserialize<T>(body.ToArray(), new JsonSerializerOptions(JsonSerializerDefaults.Web))
               ?? throw new InvalidOperationException("Archive returned no result.");
    }

    /// <summary>Uses the private server credential, never a browser-provided tenant header.</summary>
    private HttpRequestMessage Request(HttpMethod method, string path)
    {
        var request = new HttpRequestMessage(method, options.Value.GatewayUrl.TrimEnd('/') + "/" + path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", options.Value.GatewayToken);
        return request;
    }
}