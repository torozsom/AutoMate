using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Application.Abstractions.Diagnostics.Delivery;
using Infrastructure.Diagnostics.Policy;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Infrastructure.Diagnostics.Delivery;

/// <summary>
///     Finite private host failures, distinct from an authorized empty history response.
/// </summary>
public enum TelemetryCompatibilityFailure
{
    /// <summary>The destination does not expose the required archive protocol.</summary>
    IncompatibleHost,

    /// <summary>The destination is unreachable or did not complete the bounded probe.</summary>
    Unavailable,

    /// <summary>The destination rejected the server credential.</summary>
    AccessDenied
}

/// <summary>
///     Safe authored availability guidance; never retains provider URLs, bodies or inner
///     exceptions.
/// </summary>
/// <param name="failure">Finite private-host compatibility failure classification.</param>
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

/// <summary>
///     Shares bounded authenticated compatibility checks across Web scopes, with short recovery
///     caching.
/// </summary>
/// <param name="clients">Configured HTTP transport used for bounded provider requests.</param>
/// <param name="options">Provides the configured limits and endpoint settings for this service.</param>
/// <param name="logger">
///     Receives reviewed operational events without diagnostic payloads or credentials.
/// </param>
/// <param name="clock">Time source used for deterministic sampling and deadlines.</param>
public sealed class TelemetryGatewayCompatibility(
    IHttpClientFactory clients,
    IOptions<TelemetryStorageOptions> options,
    ILogger<TelemetryGatewayCompatibility> logger,
    TimeProvider? clock = null) : IDisposable
{
    private static readonly JsonSerializerOptions SReadOptions = new(JsonSerializerDefaults.Web);

    /// <summary>Coalesces concurrent probes without holding an HTTP client or response.</summary>
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>Cached finite result, without the rejected response.</summary>
    private TelemetryCompatibilityFailure? _failure;

    /// <summary>Next bounded recheck, including failures to avoid per-project probe storms.</summary>
    private DateTimeOffset _nextCheck;

    /// <summary>
    ///     Releases owned resources and completes the documented service lifetime.
    /// </summary>
    public void Dispose()
    {
        _gate.Dispose();
    }

    /// <summary>
    ///     Requires the current private protocol before any archive operation.
    /// </summary>
    /// <param name="token">Cancels the underlying operation when observed.</param>
    /// <returns>A task representing completion of the asynchronous operation.</returns>
    /// <exception cref="TelemetryCompatibilityException">
    ///     The host capability protocol or required archive operations are incompatible.
    /// </exception>
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

    /// <summary>
    ///     Bounds headers and JSON body to five seconds and 8 KiB without reading tenant evidence.
    /// </summary>
    /// <param name="token">Cancels the underlying operation when observed.</param>
    /// <returns>
    ///     A task returning the telemetry compatibility failure projection derived from the
    ///     verified inputs, or null when no value is available.
    /// </returns>
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

            switch (response.StatusCode)
            {
                case HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden:
                    return TelemetryCompatibilityFailure.AccessDenied;
                case HttpStatusCode.NotFound:
                    return TelemetryCompatibilityFailure.IncompatibleHost;
            }

            if (!response.IsSuccessStatusCode) return TelemetryCompatibilityFailure.Unavailable;
            if (response.Content.Headers.ContentLength > 8192) return TelemetryCompatibilityFailure.IncompatibleHost;

            await using var stream = await response.Content.ReadAsStreamAsync(deadline.Token);

            var bytes = new byte[8193];
            var count = 0;
            int read;

            while (count < bytes.Length && (read = await stream.ReadAsync(bytes.AsMemory(count), deadline.Token)) > 0)
                count += read;

            if (count > 8192) return TelemetryCompatibilityFailure.IncompatibleHost;

            var capabilities = JsonSerializer.Deserialize<TelemetryCapabilities>(
                bytes.AsSpan(0, count),
                SReadOptions);

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
