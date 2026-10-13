using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Application.Abstractions.Ai;
using Application.Abstractions.Diagnostics;
using Microsoft.Extensions.Options;

namespace Infrastructure.Diagnostics;

/// <summary>
///     Only server-side callers possess the private ingestion credential.
/// </summary>
/// <param name="clients">Configured HTTP transport used for bounded provider requests.</param>
/// <param name="options">Provides the configured limits and endpoint settings for this service.</param>
/// <param name="compatibility">Checks the private telemetry host capability protocol.</param>
public sealed class TelemetryGatewayClient(
    IHttpClientFactory clients,
    IOptions<TelemetryStorageOptions> options,
    TelemetryGatewayCompatibility compatibility)
    : ITelemetryGateway, IDeploymentArchive, IDeploymentAssessmentArchive
{
    /// <summary>
    ///     Returns a private owner-authorized page of numeric deployment partitions.
    /// </summary>
    /// <param name="request">The owned deployment partitions, frozen range and continuation to read.</param>
    /// <param name="token">Cancels the operation when supported by the underlying work.</param>
    /// <returns>
    ///     A task returning an authorized numeric batch, or an empty batch with an availability
    ///     notice when unsupported.
    /// </returns>
    public Task<ArchiveMetricBatch> ReadMetricBatchAsync(ArchiveMetricBatchRequest request, CancellationToken token)
    {
        return SendArchiveAsync<ArchiveMetricBatch>("archive/metric-batch", request, token);
    }

    /// <summary>
    ///     Filters before pagination and returns recorded source metadata without diagnostic prose
    ///     in catalogs.
    /// </summary>
    /// <param name="query">The frozen resource scope, selection and bounded read options.</param>
    /// <param name="token">Cancels the operation when supported by the underlying work.</param>
    /// <returns>A task returning selected bounded evidence and recorded source metadata.</returns>
    public Task<ArchiveAssessmentPage> ReadAssessmentAsync(ArchiveAssessmentQuery query, CancellationToken token)
    {
        return SendArchiveAsync<ArchiveAssessmentPage>("archive/assessment", query, token);
    }

    /// <summary>
    ///     Filters container identities before bounded metric aggregation.
    /// </summary>
    /// <param name="query">The frozen resource scope, selection and bounded read options.</param>
    /// <param name="token">Cancels the operation when supported by the underlying work.</param>
    /// <returns>
    ///     A task returning bounded metric aggregates for the selected containers and time window.
    /// </returns>
    public async Task<IReadOnlyList<DeploymentMetricPoint>> ReadAssessmentMetricsAsync(ArchiveAssessmentQuery query,
        CancellationToken token)
    {
        return await SendArchiveAsync<DeploymentMetricPoint[]>("archive/assessment-metrics", query, token);
    }

    /// <summary>
    ///     Durably appends an immutable event; duplicate identities return the original receipt.
    /// </summary>
    /// <param name="envelope">The immutable redacted event and durable routing metadata.</param>
    /// <param name="token">Cancels the operation when supported by the underlying work.</param>
    /// <returns>
    ///     A task returning the durable original receipt, including when the event identity already
    ///     exists.
    /// </returns>
    public Task<DeploymentLogEnvelope> AppendAsync(DeploymentLogEnvelope envelope, CancellationToken token)
    {
        return SendArchiveAsync<DeploymentLogEnvelope>("archive/import", envelope, token);
    }

    /// <summary>
    ///     Returns a bounded ordered log page with optional plain-text search.
    /// </summary>
    /// <param name="tenant">The internal owner-account telemetry scope.</param>
    /// <param name="project">The project identity used to scope the operation.</param>
    /// <param name="deployment">The exact deployment identity used to scope the operation.</param>
    /// <param name="cursor">The previous read position; an absent cursor selects the initial page.</param>
    /// <param name="backwards">Whether to read earlier records rather than records after the cursor.</param>
    /// <param name="limit">The maximum number of returned or processed records.</param>
    /// <param name="search">The optional plain-text search term.</param>
    /// <param name="token">Cancels the operation when supported by the underlying work.</param>
    /// <returns>A task returning a bounded ordered page of redacted log envelopes.</returns>
    public async Task<IReadOnlyList<DeploymentLogEnvelope>> ReadAsync(Guid tenant, Guid project, Guid deployment,
        long cursor, bool backwards, int limit, string? search, CancellationToken token)
    {
        return await SendArchiveAsync<DeploymentLogEnvelope[]>(
            $"archive/{tenant:N}/{project:N}/{deployment:N}?cursor={cursor}&backwards={backwards.ToString().ToLowerInvariant()}&limit={Math.Clamp(limit, 1, 2001)}&search={Uri.EscapeDataString(search ?? "")}",
            null, token);
    }

    /// <summary>
    ///     Aggregates retained numeric samples without zero-filling missing intervals.
    /// </summary>
    /// <param name="tenant">The internal owner-account telemetry scope.</param>
    /// <param name="project">The project identity used to scope the operation.</param>
    /// <param name="deployment">The exact deployment identity used to scope the operation.</param>
    /// <param name="start">The lower bound of the selected observation window.</param>
    /// <param name="end">The upper bound of the selected observation window.</param>
    /// <param name="maximumPoints">The maximum number of returned aggregate points.</param>
    /// <param name="token">Cancels the operation when supported by the underlying work.</param>
    /// <returns>
    ///     A task returning recorded bounded numeric aggregates without zero-filling missing
    ///     intervals.
    /// </returns>
    public async Task<IReadOnlyList<DeploymentMetricPoint>> ReadMetricsAsync(Guid tenant, Guid project, Guid deployment,
        DateTimeOffset start, DateTimeOffset end, int maximumPoints, CancellationToken token)
    {
        return await SendArchiveAsync<DeploymentMetricPoint[]>("archive/metrics",
            new ArchiveMetricRequest(tenant, project, deployment, start, end, Math.Clamp(maximumPoints, 1, 1000)),
            token);
    }

    /// <summary>
    ///     Removes one deleted project's partition; repeated cleanup is harmless.
    /// </summary>
    /// <param name="tenant">The internal owner-account telemetry scope.</param>
    /// <param name="project">The project identity used to scope the operation.</param>
    /// <param name="token">Cancels the operation when supported by the underlying work.</param>
    /// <returns>A task that completes after the deleted project's archived partition is removed.</returns>
    public async Task DeleteProjectAsync(Guid tenant, Guid project, CancellationToken token)
    {
        await SendArchiveAsync<JsonElement>("archive/delete",
            new ArchiveDeleteRequest(tenant, project), token);
    }

    /// <summary>
    ///     Idempotently imports still-available interval statistics from the operational backend.
    /// </summary>
    /// <param name="import">The retained interval statistics to import idempotently.</param>
    /// <param name="token">Cancels the operation when supported by the underlying work.</param>
    /// <returns>A task that completes after retained interval statistics are imported.</returns>
    public async Task ImportMetricsAsync(ArchiveMetricImport import, CancellationToken token)
    {
        await SendArchiveAsync<JsonElement>("archive/import-metrics", import, token);
    }

    /// <summary>
    ///     Returns a stable receipt only after the queue has persisted the redacted event.
    /// </summary>
    /// <param name="diagnosticEvent">Normalized diagnostic to redact before persistence or presentation.</param>
    /// <param name="channel">The stable terminal channel used for routing.</param>
    /// <param name="cancellationToken">Cancels the operation when supported by the underlying work.</param>
    /// <returns>
    ///     A task returning the stable receipt acknowledging durable storage of the redacted event.
    /// </returns>
    /// <exception cref="InvalidOperationException">Telemetry gateway returned no receipt.</exception>
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

    /// <summary>
    ///     Returns a bounded resource-scoped view with explicit loss and truncation indicators.
    /// </summary>
    /// <param name="tenant">The internal owner-account telemetry scope.</param>
    /// <param name="project">The project identity used to scope the operation.</param>
    /// <param name="deployment">The exact deployment identity used to scope the operation.</param>
    /// <param name="cancellationToken">Cancels the operation when supported by the underlying work.</param>
    /// <returns>
    ///     A task returning a bounded pending-event view with explicit loss and truncation
    ///     metadata.
    /// </returns>
    /// <exception cref="InvalidOperationException">Telemetry gateway returned no history.</exception>
    public async Task<TelemetryPendingHistory> ReadPendingAsync(Guid tenant, Guid project, Guid deployment,
        CancellationToken cancellationToken)
    {
        using var request = Request(HttpMethod.Get, $"pending/{tenant:N}/{project:N}/{deployment:N}");
        using var response = await clients.CreateClient("DeploymentTelemetry").SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<TelemetryPendingHistory>(cancellationToken) ??
               throw new InvalidOperationException("Telemetry gateway returned no history.");
    }

    /// <summary>
    ///     Bounds archive response bodies and cancellation across headers and decoding.
    /// </summary>
    /// <param name="path">Filesystem or provider entry path used by this operation.</param>
    /// <param name="payload">Serialized payload subject to bounded validation and redaction.</param>
    /// <param name="token">Cancels the underlying operation when observed.</param>
    /// <typeparam name="T">Type of the operation result.</typeparam>
    /// <returns>The supplied operation result, preserving its exception identity.</returns>
    /// <exception cref="TelemetryCompatibilityException">
    ///     The private host is incompatible or required gateway configuration is unavailable.
    /// </exception>
    /// <exception cref="InvalidOperationException">Archive response exceeds its bound.</exception>
    private async Task<T> SendArchiveAsync<T>(string path, object? payload, CancellationToken token)
    {
        await compatibility.EnsureAsync(token);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(30));
        using var request = Request(payload is null ? HttpMethod.Get : HttpMethod.Post, path);
        if (payload is not null) request.Content = JsonContent.Create(payload);
        using var response = await clients.CreateClient("DeploymentTelemetry").SendAsync(request,
            HttpCompletionOption.ResponseHeadersRead, deadline.Token);
        if (response.StatusCode == HttpStatusCode.NotFound)
            throw new TelemetryCompatibilityException(TelemetryCompatibilityFailure.IncompatibleHost);
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

    /// <summary>
    ///     Uses the private server credential, never a browser-provided tenant header.
    /// </summary>
    /// <param name="method">HTTP method used for the bounded provider request.</param>
    /// <param name="path">Filesystem or provider entry path used by this operation.</param>
    /// <returns>The configured request with bounded provider headers and protected authorization.</returns>
    private HttpRequestMessage Request(HttpMethod method, string path)
    {
        var request = new HttpRequestMessage(method, options.Value.GatewayUrl.TrimEnd('/') + "/" + path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", options.Value.GatewayToken);
        return request;
    }
}
