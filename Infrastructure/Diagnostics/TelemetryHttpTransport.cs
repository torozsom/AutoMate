using System.Diagnostics;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace Infrastructure.Diagnostics;

/// <summary>Bounds authenticated store requests without exposing response bodies or gateway credentials.</summary>
public sealed class TelemetryHttpTransport(IHttpClientFactory clients, IOptions<TelemetryStorageOptions> options)
    : IDisposable
{
    /// <summary>Canonical envelope serialization used on retries and query decoding.</summary>
    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>Bounds waiting callers as well as active requests.</summary>
    private readonly SemaphoreSlim _admission = new(options.Value.QueryConcurrency * 4);

    /// <summary>Shared process-local query capacity.</summary>
    private readonly SemaphoreSlim _queries = new(options.Value.QueryConcurrency);

    /// <inheritdoc />
    public void Dispose()
    {
        _queries.Dispose();
        _admission.Dispose();
    }

    /// <summary>Sends a tenant-scoped request and limits decoded response size.</summary>
    public async Task<JsonDocument> SendAsync(string uri, Guid tenantId, object? payload,
        CancellationToken cancellationToken)
    {
        // Include capacity waits and response-body reads in the deadline, not only receipt of HTTP headers.
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(10));
        cancellationToken = deadline.Token;
        if (!await _admission.WaitAsync(0, cancellationToken))
            throw new TelemetryProviderException(503, TimeSpan.FromSeconds(2));
        try
        {
            await _queries.WaitAsync(cancellationToken);
            var started = Stopwatch.GetTimestamp();
            try
            {
                using var request = new HttpRequestMessage(payload is null ? HttpMethod.Get : HttpMethod.Post, uri);
                request.Headers.Add("X-Scope-OrgID", tenantId.ToString("N"));
                if (!string.IsNullOrWhiteSpace(options.Value.Authorization))
                    request.Headers.TryAddWithoutValidation("Authorization", options.Value.Authorization);
                if (payload is not null)
                {
                    request.Content = JsonContent.Create(payload, options: Json);
                    // Mimir's OTLP handler requires the exact media type, without a charset parameter.
                    request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
                }

                using var response = await clients.CreateClient("DeploymentTelemetry").SendAsync(request,
                    HttpCompletionOption.ResponseHeadersRead, cancellationToken);
                if (!response.IsSuccessStatusCode)
                {
                    if ((int)response.StatusCode == 429) TelemetryStorageMetrics.Throttled.Add(1);
                    throw new TelemetryProviderException((int)response.StatusCode,
                        response.Headers.RetryAfter?.Delta ??
                        response.Headers.RetryAfter?.Date - DateTimeOffset.UtcNow);
                }

                await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
                using var buffer = new MemoryStream();
                var chunk = new byte[8192];
                int count;
                while ((count = await stream.ReadAsync(chunk, cancellationToken)) != 0)
                {
                    if (buffer.Length + count > 8 * 1024 * 1024)
                        throw new InvalidOperationException("Telemetry query exceeded its response budget.");
                    await buffer.WriteAsync(chunk.AsMemory(0, count), cancellationToken);
                }

                if (buffer.Length == 0) return JsonDocument.Parse("{}");
                return JsonDocument.Parse(buffer.ToArray());
            }
            finally
            {
                TelemetryStorageMetrics.RequestSeconds.Record(Stopwatch.GetElapsedTime(started).TotalSeconds,
                    new KeyValuePair<string, object?>("operation", payload is null ? "query" : "write"));
                _queries.Release();
            }
        }
        finally
        {
            _admission.Release();
        }
    }
}

/// <summary>Safe provider failure containing only status and retry guidance.</summary>
public sealed class TelemetryProviderException(int status, TimeSpan? retryAfter)
    : Exception($"Telemetry provider returned HTTP {status}.")
{
    public int Status { get; } = status;

    /// <summary>Provider-requested delay, bounded by the worker.</summary>
    public TimeSpan? RetryAfter { get; } = retryAfter;
}