using Application.Abstractions.Diagnostics.Metrics;
using Domain.Enums.Deployments;
using Domain.Enums.Projects;

namespace Application.Data.Apps;

/// <summary>
///     Owner-scoped, bounded read models for the Overview and Projects pages.
/// </summary>
public interface IWorkspaceQuery
{
    /// <summary>
    ///     Reads current workspace state and recorded activity over 7, 30 or 90 UTC days.
    /// </summary>
    /// <param name="owner">The internal owner account identity used for authorization.</param>
    /// <param name="days">The supported workspace lookback: 7, 30 or 90 days.</param>
    /// <param name="token">Cancels the operation when supported by the underlying work.</param>
    /// <returns>
    ///     A task returning owner-scoped recorded deployment activity and observed resource
    ///     summaries.
    /// </returns>
    Task<WorkspaceOverview> OverviewAsync(Guid owner, int days, CancellationToken token = default);

    /// <summary>
    ///     Reads a frozen UTC window with bounded chart aggregation.
    /// </summary>
    /// <param name="owner">The internal owner account identity used for authorization.</param>
    /// <param name="range">The selected frozen observation window or assessment range.</param>
    /// <param name="token">Cancels the operation when supported by the underlying work.</param>
    /// <returns>
    ///     A task returning owner-scoped recorded deployment activity and observed resource
    ///     summaries.
    /// </returns>
    Task<WorkspaceOverview> OverviewAsync(Guid owner, MetricTimeRange range, CancellationToken token = default)
    {
        return OverviewAsync(owner, (int)Math.Ceiling((range.End - range.Start).TotalDays), token);
    }

    /// <summary>
    ///     Searches and pages owned projects without loading entity graphs or credentials.
    /// </summary>
    /// <param name="owner">The internal owner account identity used for authorization.</param>
    /// <param name="request">The owner-scoped inventory search, filters, sort and page.</param>
    /// <param name="token">Cancels the operation when supported by the underlying work.</param>
    /// <returns>A task returning a bounded owner-scoped inventory page and owner totals.</returns>
    Task<ProjectInventoryPage> ProjectsAsync(Guid owner, ProjectInventoryRequest request,
        CancellationToken token = default);
}

/// <summary>
///     URL-compatible inventory selection. Page size is fixed at twenty.
/// </summary>
/// <param name="Search">The optional plain-text search term.</param>
/// <param name="Source">The optional local or GitHub source filter.</param>
/// <param name="Status">The recorded deployment runtime state.</param>
/// <param name="Sort">The supported inventory sort key.</param>
/// <param name="Page">The one-based inventory page number.</param>
public sealed record ProjectInventoryRequest(
    string? Search = null,
    SourceType? Source = null,
    DeploymentStatus? Status = null,
    string Sort = "activity",
    int Page = 1);

/// <summary>
///     Compact project row containing only navigation and display metadata.
/// </summary>
/// <param name="Id">The record identity.</param>
/// <param name="Name">The recorded display name or fixed metric identity.</param>
/// <param name="Source">The recorded source display label.</param>
/// <param name="SourceType">The recorded local or GitHub source classification.</param>
/// <param name="Components">The recorded component count.</param>
/// <param name="WebApps">The recorded web application count.</param>
/// <param name="SavedAt">The UTC time at which the project was saved.</param>
/// <param name="LatestAt">The latest recorded deployment time, if any.</param>
/// <param name="Status">The recorded deployment runtime state.</param>
public sealed record ProjectInventoryRow(
    Guid Id,
    string Name,
    string Source,
    SourceType SourceType,
    int Components,
    int WebApps,
    DateTimeOffset SavedAt,
    DateTimeOffset? LatestAt,
    DeploymentStatus? Status);

/// <summary>
///     Bounded inventory and unfiltered owner counts.
/// </summary>
/// <param name="Items">The bounded page of selected rows.</param>
/// <param name="Total">The total matching row count before pagination.</param>
/// <param name="Page">The one-based inventory page number.</param>
/// <param name="SavedProjects">The owner total of saved projects.</param>
/// <param name="RunningProjects">The owner total of projects with a running latest deployment.</param>
/// <param name="GitHubProjects">The owner total of saved GitHub projects.</param>
/// <param name="LocalProjects">The owner total of saved local projects.</param>
public sealed record ProjectInventoryPage(
    IReadOnlyList<ProjectInventoryRow> Items,
    int Total,
    int Page,
    int SavedProjects,
    int RunningProjects,
    int GitHubProjects,
    int LocalProjects);

/// <summary>
///     Selected deployment metadata; runtime and completion outcome are distinct.
/// </summary>
/// <param name="ProjectId">The project identity used for authorization or correlation.</param>
/// <param name="DeploymentId">The exact deployment identity used for ownership or correlation.</param>
/// <param name="Project">The recorded project display name.</param>
/// <param name="Provider">The provider identity; not a credential or endpoint.</param>
/// <param name="StartedAt">The recorded deployment start time.</param>
/// <param name="Outcome">The finite recorded operation or deployment outcome.</param>
/// <param name="Status">The recorded deployment runtime state.</param>
/// <param name="DurationSeconds">The recorded completion duration in seconds, if available.</param>
public sealed record WorkspaceRun(
    Guid ProjectId,
    Guid DeploymentId,
    string Project,
    string Provider,
    DateTimeOffset StartedAt,
    DeploymentOutcome Outcome,
    DeploymentStatus Status,
    double? DurationSeconds);

/// <summary>
///     Persisted UTC activity bucket, including unknown outcomes.
/// </summary>
/// <param name="Day">The UTC day represented by the activity bucket.</param>
/// <param name="Succeeded">The recorded successful-outcome count.</param>
/// <param name="Failed">The recorded failed-outcome count.</param>
/// <param name="Unknown">The recorded outcome count whose result is unknown.</param>
public sealed record WorkspaceActivity(DateTimeOffset Day, int Succeeded, int Failed, int Unknown);

/// <summary>
///     Sample-weighted observed container usage, never a fleet capacity estimate.
/// </summary>
/// <param name="Day">The UTC day represented by the activity bucket.</param>
/// <param name="Metric">The fixed metric-series name.</param>
/// <param name="Unit">The native metric unit associated with the sample.</param>
/// <param name="Samples">The number of observed samples represented by the aggregate.</param>
/// <param name="Average">The observed arithmetic mean; absent values remain unknown.</param>
/// <param name="Minimum">The observed minimum value.</param>
/// <param name="Maximum">The supported upper bound.</param>
/// <param name="Incomplete">Whether the returned observations have known coverage gaps.</param>
public sealed record WorkspaceResource(
    DateTimeOffset Day,
    string Metric,
    string Unit,
    long Samples,
    double Average,
    double? Minimum,
    double? Maximum,
    bool Incomplete);

/// <summary>
///     Actionable persisted state, including cloud work not yet associated with a deployment.
/// </summary>
/// <param name="ProjectId">The project identity used for authorization or correlation.</param>
/// <param name="Project">The recorded project display name.</param>
/// <param name="State">The project queue-state snapshot or fixed attention state.</param>
/// <param name="DeploymentId">
///     The deployment identity; null is used only where legacy observations permit it.
/// </param>
public sealed record WorkspaceAttention(Guid ProjectId, string Project, string State, Guid? DeploymentId);

/// <summary>
///     Independent telemetry availability accompanies otherwise usable deployment information.
/// </summary>
/// <param name="Start">The lower bound of the selected observation window.</param>
/// <param name="End">The upper bound of the selected observation window.</param>
/// <param name="SavedProjects">The owner total of saved projects.</param>
/// <param name="RunningDeployments">The current recorded running-deployment count.</param>
/// <param name="Attempts">The recorded deployment-attempt count.</param>
/// <param name="Succeeded">The recorded successful-outcome count.</param>
/// <param name="Failed">The recorded failed-outcome count.</param>
/// <param name="Unknown">The recorded outcome count whose result is unknown.</param>
/// <param name="AverageDurationSeconds">
///     The mean recorded completed-deployment duration, if available.
/// </param>
/// <param name="Activity">Recorded deployment outcomes grouped by UTC day.</param>
/// <param name="Resources">Recorded resource statistics grouped by UTC day.</param>
/// <param name="Recent">The bounded list of recently recorded deployments.</param>
/// <param name="Attention">Projects with persisted states that require owner attention.</param>
/// <param name="ResourceNotice">A safe metric availability or coverage notice, if present.</param>
/// <param name="Observations">Optional detailed numeric exploration results.</param>
public sealed record WorkspaceOverview(
    DateTimeOffset Start,
    DateTimeOffset End,
    int SavedProjects,
    int RunningDeployments,
    int Attempts,
    int Succeeded,
    int Failed,
    int Unknown,
    double? AverageDurationSeconds,
    IReadOnlyList<WorkspaceActivity> Activity,
    IReadOnlyList<WorkspaceResource> Resources,
    IReadOnlyList<WorkspaceRun> Recent,
    IReadOnlyList<WorkspaceAttention> Attention,
    string? ResourceNotice,
    MetricExplorationResult? Observations = null);
