using Domain.Entities.Deployments;

namespace Application.Data.Apps;

/// <summary>
///     Recorded deployment totals, independent of the bounded current-state graph and history page.
/// </summary>
/// <param name="Total">The number of all recorded deployments for the owned application.</param>
/// <param name="Succeeded">The count of recorded preparation successes, including subsequently stopped deployments.</param>
/// <param name="Failed">The count of recorded preparation failures.</param>
/// <param name="Unknown">The count whose recorded outcome remains unknown.</param>
/// <param name="FirstCreatedAt">The earliest deployment creation time, or null when no history exists.</param>
public sealed record ProjectDeploymentSummary(
    int Total,
    int Succeeded,
    int Failed,
    int Unknown,
    DateTimeOffset? FirstCreatedAt);

/// <summary>
///     Detached project metadata and current deployment identities, plus independent totals and a six-row history page.
/// </summary>
/// <param name="Project">
///     The existing domain-shaped compatibility view with only newest deployment timestamps per C#
///     project.
/// </param>
/// <param name="Summary">Server-side aggregates over the complete owned history.</param>
/// <param name="History">The selected six-row history page in existing creation/identity order.</param>
public sealed record ProjectDetailsReadModel(
    Domain.Entities.Projects.Application Project,
    ProjectDeploymentSummary Summary,
    IReadOnlyList<Deployment> History);

/// <summary>
///     Reads owned current project metadata without materializing its permanent deployment history.
/// </summary>
public interface IProjectDetailsQuery
{
    /// <summary>
    ///     Reads current metadata, all-history aggregates and the requested six-row page.
    /// </summary>
    /// <param name="project">The application identity.</param>
    /// <param name="owner">The internal owner used by every database query.</param>
    /// <param name="historyPage">The existing zero-based page index.</param>
    /// <param name="token">Cancels the operation's database reads.</param>
    /// <returns>The detached view, or null when the project is absent or not owned.</returns>
    Task<ProjectDetailsReadModel?> ReadAsync(Guid project, Guid owner, int historyPage,
        CancellationToken token = default);
}

/// <summary>
///     Supplies detached owned metadata for deploy/stop actions without reading permanent history or its aggregates.
/// </summary>
public interface IProjectActionQuery
{
    /// <summary>
    ///     Reads project configuration and deployments tied at each C# project's newest timestamp.
    /// </summary>
    /// <param name="project">The application identity.</param>
    /// <param name="owner">The internal owner used by the database read.</param>
    /// <param name="token">Cancels the metadata read.</param>
    /// <returns>The detached metadata graph, or null for an inaccessible application.</returns>
    Task<Domain.Entities.Projects.Application?> GetAsync(Guid project, Guid owner, CancellationToken token = default);
}
