using System.Net.Http.Headers;
using System.Net.Http.Json;
using Application.Abstractions.Diagnostics;
using Microsoft.Extensions.Options;

namespace Infrastructure.Diagnostics;

/// <summary>Only server-side callers possess the private ingestion credential.</summary>
public sealed class TelemetryGatewayClient(IHttpClientFactory clients, IOptions<TelemetryStorageOptions> options)
    : ITelemetryGateway
{
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

    private HttpRequestMessage Request(HttpMethod method, string path)
    {
        var request = new HttpRequestMessage(method, options.Value.GatewayUrl.TrimEnd('/') + "/" + path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", options.Value.GatewayToken);
        return request;
    }
}