namespace Application.Abstractions.Diagnostics;

/// <summary>
///     Retained daily sufficient statistics for one container metric; sample counts support
///     weighted averages.
/// </summary>
/// <param name="Container">
///     The recorded container identity; null selects all containers where supported.
/// </param>
/// <param name="Name">The recorded display name or fixed metric identity.</param>
/// <param name="Unit">The native metric unit associated with the sample.</param>
/// <param name="SampleCount">The number of underlying observations represented by the statistics.</param>
/// <param name="Sum">
///     The sum of observed sample values, used with sample count for weighted averages.
/// </param>
/// <param name="Minimum">The observed minimum in the recorded native unit.</param>
/// <param name="Maximum">The observed maximum in the recorded native unit.</param>
public sealed record DailyMetricStatistics(
    string Container,
    string Name,
    string Unit,
    long SampleCount,
    double Sum,
    double Minimum,
    double Maximum);

/// <summary>
///     Reads retained daily metric statistics for one tenant, project and deployment.
/// </summary>
public interface IDailyDeploymentMetricQuery
{
    /// <summary>
    ///     Reads recorded daily container metric statistics within the selected window.
    /// </summary>
    /// <param name="tenant">The internal owner-account telemetry scope.</param>
    /// <param name="project">The project identity used to scope the operation.</param>
    /// <param name="deployment">The exact deployment identity used to scope the operation.</param>
    /// <param name="start">The lower bound of the selected observation window.</param>
    /// <param name="end">The upper bound of the selected observation window.</param>
    /// <param name="token">Cancels the operation when supported by the underlying work.</param>
    /// <returns>
    ///     A task returning recorded daily sufficient statistics without synthesizing missing
    ///     observations.
    /// </returns>
    Task<IReadOnlyList<DailyMetricStatistics>> ReadDailyAsync(Guid tenant, Guid project, Guid deployment,
        DateTimeOffset start, DateTimeOffset end, CancellationToken token);
}

/// <summary>
///     Counts observed deployment log errors within an absolute time window.
/// </summary>
public interface IDeploymentErrorCountQuery
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
    Task<long> CountErrorsAsync(Guid tenant, Guid project, Guid deployment, DateTimeOffset start,
        DateTimeOffset end, CancellationToken token);
}

/// <summary>
///     Daily deployment/container observations with optional metric values and observed error
///     counts.
/// </summary>
/// <param name="DeploymentId">The exact deployment identity used for ownership or correlation.</param>
/// <param name="DayUtc">The UTC day represented by the aggregate.</param>
/// <param name="Container">
///     The recorded container identity; null selects all containers where supported.
/// </param>
/// <param name="Metric">The fixed metric-series name.</param>
/// <param name="Unit">The native metric unit associated with the sample.</param>
/// <param name="SampleCount">The number of underlying observations represented by the statistics.</param>
/// <param name="Average">The observed arithmetic mean; absent values remain unknown.</param>
/// <param name="Minimum">The observed minimum value.</param>
/// <param name="Maximum">The supported upper bound.</param>
/// <param name="ObservedErrors">The number of observed error records represented by the row.</param>
/// <param name="Incomplete">Whether the returned observations have known coverage gaps.</param>
/// <param name="UpdatedAt">The UTC time at which the aggregate view was updated.</param>
public sealed record DeploymentAnalyticsRow(
    Guid DeploymentId,
    DateTimeOffset DayUtc,
    string Container,
    string Metric,
    string Unit,
    long SampleCount,
    double? Average,
    double? Minimum,
    double? Maximum,
    long ObservedErrors,
    bool Incomplete,
    DateTimeOffset UpdatedAt);

/// <summary>
///     Project deployment outcomes and daily telemetry, with independent availability
///     information.
/// </summary>
/// <param name="Deployments">The number of recorded deployments in the selected period.</param>
/// <param name="SuccessfulDeployments">The recorded successful-deployment count.</param>
/// <param name="FailedDeployments">The recorded failed-deployment count.</param>
/// <param name="AverageDeploymentSeconds">
///     The mean recorded completed-deployment duration, if available.
/// </param>
/// <param name="Daily">Recorded daily deployment/container telemetry rows.</param>
/// <param name="Availability">
///     A safe availability or coverage notice; null indicates no reported notice.
/// </param>
/// <param name="Observations">Optional detailed numeric exploration results.</param>
/// <param name="CompleteDayLogErrors">The observed error count for complete UTC days, if available.</param>
public sealed record ProjectTelemetryAnalytics(
    int Deployments,
    int SuccessfulDeployments,
    int FailedDeployments,
    double? AverageDeploymentSeconds,
    IReadOnlyList<DeploymentAnalyticsRow> Daily,
    string? Availability,
    MetricExplorationResult? Observations = null,
    long? CompleteDayLogErrors = null);

/// <summary>
///     Reads owner-authorized project analytics without exposing provider credentials.
/// </summary>
public interface IProjectTelemetryAnalytics
{
    /// <summary>
    ///     Reads owned project outcomes and daily observations for an absolute UTC window.
    /// </summary>
    /// <param name="user">The internal user identity used for authorization.</param>
    /// <param name="project">The project identity used to scope the operation.</param>
    /// <param name="start">The lower bound of the selected observation window.</param>
    /// <param name="end">The upper bound of the selected observation window.</param>
    /// <param name="token">Cancels the operation when supported by the underlying work.</param>
    /// <returns>
    ///     A task returning recorded project outcomes and daily observations with independent
    ///     availability notices.
    /// </returns>
    Task<ProjectTelemetryAnalytics> ReadAsync(Guid user, Guid project, DateTimeOffset start, DateTimeOffset end,
        CancellationToken token = default);
}