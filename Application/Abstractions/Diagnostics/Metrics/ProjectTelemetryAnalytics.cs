namespace Application.Abstractions.Diagnostics;

public sealed record DailyMetricStatistics(
    string Container,
    string Name,
    string Unit,
    long SampleCount,
    double Sum,
    double Minimum,
    double Maximum);

public interface IDailyDeploymentMetricQuery
{
    Task<IReadOnlyList<DailyMetricStatistics>> ReadDailyAsync(Guid tenant, Guid project, Guid deployment,
        DateTimeOffset start, DateTimeOffset end, CancellationToken token);
}

public interface IDeploymentErrorCountQuery
{
    Task<long> CountErrorsAsync(Guid tenant, Guid project, Guid deployment, DateTimeOffset start,
        DateTimeOffset end, CancellationToken token);
}

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

public sealed record ProjectTelemetryAnalytics(
    int Deployments,
    int SuccessfulDeployments,
    int FailedDeployments,
    double? AverageDeploymentSeconds,
    IReadOnlyList<DeploymentAnalyticsRow> Daily,
    string? Availability,
    MetricExplorationResult? Observations = null,
    long? CompleteDayLogErrors = null);

public interface IProjectTelemetryAnalytics
{
    Task<ProjectTelemetryAnalytics> ReadAsync(Guid user, Guid project, DateTimeOffset start, DateTimeOffset end,
        CancellationToken token = default);
}