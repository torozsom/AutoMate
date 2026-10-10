using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Application.Abstractions.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Infrastructure.Diagnostics;

/// <summary>Finite private host failures, distinct from an authorized empty history response.</summary>
public enum TelemetryCompatibilityFailure
{
    /// <summary>The destination does not expose the required archive protocol.</summary>
    IncompatibleHost,

    /// <summary>The destination is unreachable or did not complete the bounded probe.</summary>
    Unavailable,

    /// <summary>The destination rejected the server credential.</summary>
    AccessDenied
}

/// <summary>Safe authored availability guidance; never retains provider URLs, bodies or inner exceptions.</summary>
public sealed class TelemetryCompatibilityException(TelemetryCompatibilityFailure failure) : InvalidOperationException(
    failure switch
    {
        TelemetryCompatibilityFailure.IncompatibleHost =>
            "Saved diagnostics require an updated Telemetry host. Ask the operator to update it, then refresh.",
        TelemetryCompatibilityFailure.AccessDenied =>
            "Saved diagnostic storage denied access. Ask the operator to check the Telemetry connection.",
        _ => "Saved diagnostic storage is temporarily unavailable. Refresh to retry."
    })
{
    /// <summary>Finite operational category without configuration or payload details.</summary>
    public TelemetryCompatibilityFailure Failure { get; } = failure;
}

/// <summary>Shares bounded authenticated compatibility checks across Web scopes, with short recovery caching.</summary>
public sealed class TelemetryGatewayCompatibility(
    IHttpClientFactory clients,
    IOptions<TelemetryStorageOptions> options,
    ILogger<TelemetryGatewayCompatibility> logger,
    TimeProvider? clock = null) : IDisposable
{
    /// <summary>Coalesces concurrent probes without holding an HTTP client or response.</summary>
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>Cached finite result, without the rejected response.</summary>
    private TelemetryCompatibilityFailure? _failure;

    /// <summary>Next bounded recheck, including failures to avoid per-project probe storms.</summary>
    private DateTimeOffset _nextCheck;

    /// <inheritdoc />
    public void Dispose()
    {
        _gate.Dispose();
    }

    /// <summary>Requires the current private protocol before any archive operation.</summary>
    public async Task EnsureAsync(CancellationToken token)
    {
        await _gate.WaitAsync(token);
        try
        {
            var now = (clock ?? TimeProvider.System).GetUtcNow();
            if (now >= _nextCheck)
            {
                _failure = await ProbeAsync(token);
                _nextCheck = now.AddSeconds(_failure is null ? 60 : 5);
                if (_failure is { } failed)
                {
                    var error = new TelemetryCompatibilityException(failed);
                    logger.LogWarning(error, "Telemetry archive compatibility failed: {TelemetryCompatibility}.",
                        failed);
                }
            }

            if (_failure is { } failure) throw new TelemetryCompatibilityException(failure);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Bounds headers and JSON body to five seconds and 8 KiB without reading tenant evidence.</summary>
    private async Task<TelemetryCompatibilityFailure?> ProbeAsync(CancellationToken token)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(5));
        using var request =
            new HttpRequestMessage(HttpMethod.Get, options.Value.GatewayUrl.TrimEnd('/') + "/capabilities");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", options.Value.GatewayToken);
        try
        {
            using var response = await clients.CreateClient("DeploymentTelemetry").SendAsync(request,
                HttpCompletionOption.ResponseHeadersRead, deadline.Token);
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                return TelemetryCompatibilityFailure.AccessDenied;
            if (response.StatusCode == HttpStatusCode.NotFound) return TelemetryCompatibilityFailure.IncompatibleHost;
            if (!response.IsSuccessStatusCode) return TelemetryCompatibilityFailure.Unavailable;
            if (response.Content.Headers.ContentLength > 8192) return TelemetryCompatibilityFailure.IncompatibleHost;
            await using var stream = await response.Content.ReadAsStreamAsync(deadline.Token);
            var bytes = new byte[8193];
            var count = 0;
            int read;
            while (count < bytes.Length && (read = await stream.ReadAsync(bytes.AsMemory(count), deadline.Token)) > 0)
                count += read;
            if (count > 8192) return TelemetryCompatibilityFailure.IncompatibleHost;
            var capabilities = JsonSerializer.Deserialize<TelemetryCapabilities>(bytes.AsSpan(0, count),
                new JsonSerializerOptions(JsonSerializerDefaults.Web));
            return capabilities is { Version: >= TelemetryCapabilities.CurrentVersion, ArchiveOperations: not null } &&
                   TelemetryCapabilities.RequiredArchiveOperations.All(operation =>
                       capabilities.ArchiveOperations.Contains(operation, StringComparer.Ordinal))
                ? null
                : TelemetryCompatibilityFailure.IncompatibleHost;
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            return TelemetryCompatibilityFailure.Unavailable;
        }
        catch (HttpRequestException)
        {
            return TelemetryCompatibilityFailure.Unavailable;
        }
        catch (IOException)
        {
            return TelemetryCompatibilityFailure.Unavailable;
        }
        catch (JsonException)
        {
            return TelemetryCompatibilityFailure.IncompatibleHost;
        }
    }
}