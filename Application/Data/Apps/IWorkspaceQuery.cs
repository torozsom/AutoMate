using Application.Abstractions.Diagnostics;
using Domain.Enums;

namespace Application.Data.Apps;

/// <summary>Owner-scoped, bounded read models for the Overview and Projects pages.</summary>
public interface IWorkspaceQuery
{
    /// <summary>Reads current workspace state and recorded activity over 7, 30 or 90 UTC days.</summary>
    Task<WorkspaceOverview> OverviewAsync(Guid owner, int days, CancellationToken token = default);

    /// <summary>Reads a frozen UTC window with bounded chart aggregation.</summary>
    Task<WorkspaceOverview> OverviewAsync(Guid owner, MetricTimeRange range, CancellationToken token = default)
    {
        return OverviewAsync(owner, (int)Math.Ceiling((range.End - range.Start).TotalDays), token);
    }

    /// <summary>Searches and pages owned projects without loading entity graphs or credentials.</summary>
    Task<ProjectInventoryPage> ProjectsAsync(Guid owner, ProjectInventoryRequest request,
        CancellationToken token = default);
}

/// <summary>URL-compatible inventory selection. Page size is fixed at twenty.</summary>
public sealed record ProjectInventoryRequest(
    string? Search = null,
    SourceType? Source = null,
    DeploymentStatus? Status = null,
    string Sort = "activity",
    int Page = 1);

/// <summary>Compact project row containing only navigation and display metadata.</summary>
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

/// <summary>Bounded inventory and unfiltered owner counts.</summary>
public sealed record ProjectInventoryPage(
    IReadOnlyList<ProjectInventoryRow> Items,
    int Total,
    int Page,
    int SavedProjects,
    int RunningProjects,
    int GitHubProjects,
    int LocalProjects);

/// <summary>Selected deployment metadata; runtime and completion outcome are distinct.</summary>
public sealed record WorkspaceRun(
    Guid ProjectId,
    Guid DeploymentId,
    string Project,
    string Provider,
    DateTimeOffset StartedAt,
    DeploymentOutcome Outcome,
    DeploymentStatus Status,
    double? DurationSeconds);

/// <summary>Persisted UTC activity bucket, including unknown outcomes.</summary>
public sealed record WorkspaceActivity(DateTimeOffset Day, int Succeeded, int Failed, int Unknown);

/// <summary>Sample-weighted observed container usage, never a fleet capacity estimate.</summary>
public sealed record WorkspaceResource(
    DateTimeOffset Day,
    string Metric,
    string Unit,
    long Samples,
    double Average,
    double? Minimum,
    double? Maximum,
    bool Incomplete);

/// <summary>Actionable persisted state, including cloud work not yet associated with a deployment.</summary>
public sealed record WorkspaceAttention(Guid ProjectId, string Project, string State, Guid? DeploymentId);

/// <summary>Independent telemetry availability accompanies otherwise usable deployment information.</summary>
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