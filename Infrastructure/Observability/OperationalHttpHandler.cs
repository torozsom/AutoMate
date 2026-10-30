using System.Diagnostics;
using Application.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Infrastructure.Observability;

/// <summary>
///     Finite application-owned HTTP client families; endpoints never become telemetry labels.
/// </summary>
public enum ReviewedHttpClient
{
    /// <summary>Unrecognized client registration.</summary>
    Unknown,

    /// <summary>Azure management queries.</summary>
    AzureRuntime,

    /// <summary>Azure response-header acquisition for live streams.</summary>
    AzureLiveLogs,

    /// <summary>GitHub API and credential operations.</summary>
    GitHub,

    /// <summary>Approved AI provider operations.</summary>
    Analysis,

    /// <summary>Private telemetry ingestion and backend queries.</summary>
    DeploymentTelemetry
}

/// <summary>
///     Bounds repeated operational warnings across pooled handler instances using fixed enum keys.
/// </summary>
/// <param name="clock">Monotonic time source; defaults to the host clock.</param>
public sealed class OperationalWarningLimiter(TimeProvider? clock = null)
{
    /// <summary>Shared monotonic warning clock.</summary>
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;

    /// <summary>Last admitted warning timestamp per finite family/outcome.</summary>
    private readonly Dictionary<(ReviewedHttpClient, string), long> _last = [];

    /// <summary>
    ///     Admits the first warning and one repeat per thirty monotonic seconds.
    /// </summary>
    /// <param name="family">Reviewed client family.</param>
    /// <param name="outcome">Internal fixed outcome.</param>
    /// <returns>Whether the event may be logged.</returns>
    internal bool Admit(ReviewedHttpClient family, string outcome)
    {
        lock (_last)
        {
            var key = (family, outcome);
            var now = _clock.GetTimestamp();
            if (_last.TryGetValue(key, out var previous) && _clock.GetElapsedTime(previous, now).TotalSeconds < 30)
                return false;
            _last[key] = now;
            return true;
        }
    }
}

/// <summary>
///     Measures headers and safe outcomes without inspecting URI, payload, headers or exception messages.
/// </summary>
/// <param name="family">Reviewed registration-owned client family.</param>
/// <param name="limiter">Host-wide warning suppression.</param>
/// <param name="logger">Safe platform logger.</param>
public sealed class OperationalHttpHandler(
    ReviewedHttpClient family,
    OperationalWarningLimiter limiter,
    ILogger<OperationalHttpHandler> logger) : DelegatingHandler
{
    /// <summary>Normalized finite client identity.</summary>
    private readonly ReviewedHttpClient _family = Enum.IsDefined(family) ? family : ReviewedHttpClient.Unknown;

    /// <summary>
    ///     Counts all header-acquisition outcomes even when a repeated warning is suppressed.
    /// </summary>
    /// <param name="request">Request passed through without diagnostic inspection.</param>
    /// <param name="cancellationToken">Caller or configured timeout cancellation.</param>
    /// <returns>The unchanged response.</returns>
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        var started = Stopwatch.GetTimestamp();
        var outcome = "completed";
        var statusClass = "none";
        var statusCode = 0;
        try
        {
            var response = await base.SendAsync(request, cancellationToken);
            statusCode = (int)response.StatusCode;
            statusClass = (statusCode / 100) switch
            {
                1 => "1xx",
                2 => "2xx",
                3 => "3xx",
                4 => "4xx",
                5 => "5xx",
                _ => "other"
            };
            if (!response.IsSuccessStatusCode) outcome = "unsuccessful";
            else if (Stopwatch.GetElapsedTime(started).TotalMilliseconds >= 1000) outcome = "slow";
            return response;
        }
        catch (OperationCanceledException)
        {
            outcome = "canceled";
            throw;
        }
        catch (HttpRequestException)
        {
            outcome = "transport_failure";
            throw;
        }
        catch (Exception)
        {
            outcome = "transport_failure";
            throw;
        }
        finally
        {
            var stage = _family == ReviewedHttpClient.AzureLiveLogs ? "stream_headers" : "request_headers";
            OperationalMetrics.Record(_family.ToString(), stage, outcome, started, statusClass);
            if (outcome is not ("completed" or "canceled") && limiter.Admit(_family, outcome))
                logger.LogWarning(
                    "HTTP operation {ClientFamily} outcome {HttpOutcome}, status {StatusCode}, status class {StatusClass}, duration {ElapsedMilliseconds} ms.",
                    _family.ToString(), outcome, statusCode, statusClass,
                    Stopwatch.GetElapsedTime(started).TotalMilliseconds);
        }
    }
}

/// <summary>
///     Attaches operational observation only to explicitly reviewed application clients.
/// </summary>
public static class OperationalHttpRegistration
{
    /// <summary>
    ///     Adds a new handler instance to the application's pooled HTTP pipeline.
    /// </summary>
    /// <param name="builder">Application client registration, never an exporter client.</param>
    /// <param name="family">Trusted finite client family.</param>
    /// <returns>The same client builder.</returns>
    public static IHttpClientBuilder ObserveOperations(this IHttpClientBuilder builder, ReviewedHttpClient family)
    {
        return builder.AddHttpMessageHandler(sp => new OperationalHttpHandler(family,
            sp.GetRequiredService<OperationalWarningLimiter>(),
            sp.GetRequiredService<ILogger<OperationalHttpHandler>>()));
    }
}
