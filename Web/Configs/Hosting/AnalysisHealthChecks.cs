using Application.Abstractions.Ai;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Web.Configs;

/// <summary>
///     Maps safe Application readiness to host health without provider requests or exception
///     disclosure.
/// </summary>
/// <param name="readiness">Reads safe analysis dependency readiness without making provider requests.</param>
public sealed class AnalysisReadinessHealthCheck(IDeploymentAnalysisReadiness readiness) : IHealthCheck
{
    /// <summary>
    ///     Maps safe analysis readiness to a dependency-health result without exposing provider
    ///     responses.
    /// </summary>
    /// <param name="context">Framework health-check registration context.</param>
    /// <param name="cancellationToken">Cancels the current read, request or delivery operation.</param>
    /// <returns>
    ///     A task returning healthy, degraded or unhealthy status with finite readiness data; dependency
    ///     exceptions become unhealthy status.
    /// </returns>
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var snapshot = await readiness.CheckAsync(cancellationToken);
            var status = !snapshot.QueueAvailable || snapshot.Configuration == AnalysisConfigurationState.Invalid
                ? HealthStatus.Unhealthy
                : snapshot.Configuration is AnalysisConfigurationState.Ready or AnalysisConfigurationState.Disabled
                    ? HealthStatus.Healthy
                    : HealthStatus.Degraded;
            return new HealthCheckResult(status, data: new Dictionary<string, object> { ["readiness"] = snapshot });
        }
        catch (Exception)
        {
            // Includes timeout/cancellation and dependency-resolution failures inside the application port.
            return HealthCheckResult.Unhealthy("AI readiness is unavailable.");
        }
    }
}

/// <summary>
///     Separates dependency readiness from existing lightweight liveness and serializes only finite
///     safe fields.
/// </summary>
public static class AnalysisHealthChecks
{
    /// <summary>Tag used to select the bounded AI check independently of liveness.</summary>
    public const string ReadinessTag = "ai-ready";

    /// <summary>
    ///     Maps liveness plus AI readiness; intentionally disabled AI is healthy if queue metadata
    ///     remains available.
    /// </summary>
    /// <param name="app">Application host to configure.</param>
    public static void MapApplicationHealthChecks(this WebApplication app)
    {
        app.MapHealthChecks("/health", new HealthCheckOptions
        {
            Predicate = registration => !registration.Tags.Contains(ReadinessTag)
        });
        app.MapHealthChecks("/health/ready", new HealthCheckOptions
        {
            Predicate = registration => registration.Tags.Contains(ReadinessTag),
            ResultStatusCodes =
            {
                [HealthStatus.Healthy] = StatusCodes.Status200OK,
                [HealthStatus.Degraded] = StatusCodes.Status503ServiceUnavailable,
                [HealthStatus.Unhealthy] = StatusCodes.Status503ServiceUnavailable
            },
            ResponseWriter = WriteReadinessAsync
        });
    }

    /// <summary>
    ///     Omits framework descriptions, exceptions and arbitrary check data from the public probe
    ///     response.
    /// </summary>
    /// <param name="context">Current probe request and response; authentication is not required by this mapping.</param>
    /// <param name="report">Health results containing only approved public status fields.</param>
    /// <returns>A task representing completion of this operation, including any awaited callbacks.</returns>
    private static Task WriteReadinessAsync(HttpContext context, HealthReport report)
    {
        var snapshot = report.Entries.TryGetValue("ai_analysis", out var entry) &&
                       entry.Data.TryGetValue("readiness", out var value) &&
                       value is DeploymentAnalysisReadiness result
            ? result
            : new DeploymentAnalysisReadiness(AnalysisConfigurationState.Unavailable, false);
        var configuration = Enum.IsDefined(snapshot.Configuration)
            ? snapshot.Configuration.ToString()
            : AnalysisConfigurationState.Unavailable.ToString();
        return context.Response.WriteAsJsonAsync(new
        {
            status = report.Status.ToString(),
            configuration,
            queue = snapshot.QueueAvailable ? "Available" : "Unavailable"
        }, context.RequestAborted);
    }
}
