using Domain.DTO.Deployment;
using Domain.DTO.Projects;
using Domain.Enums.Deployments;

namespace Application.Orchestration.Cloud;

/// <summary>
///     Inputs for an authorized, durable SaaS cloud deployment request.
/// </summary>
/// <param name="UserId">The internal AutoMate user identity.</param>
/// <param name="ProjectId">The project identity used for authorization or correlation.</param>
/// <param name="IdempotencyKey">The owner-scoped stable key used to reuse a prior admission.</param>
/// <param name="RepositoryOwner">The GitHub repository owner or organization.</param>
/// <param name="RepositoryName">The GitHub repository name.</param>
/// <param name="UserGitHubAccessToken">
///     The initiating GitHub credential; it must not be retained in queue metadata or
///     diagnostics.
/// </param>
/// <param name="Config">The deployment configuration used by the operation.</param>
/// <param name="Metadata">
///     The non-secret project metadata supplied to evidence or artifact generation.
/// </param>
/// <param name="CsProjectName">The selected C# project name used for generated artifacts.</param>
/// <param name="RepositoryRoot">The selected repository root used for deployment artifact paths.</param>
public sealed record CloudDeploymentStart(
    Guid UserId,
    Guid ProjectId,
    string IdempotencyKey,
    string RepositoryOwner,
    string RepositoryName,
    string UserGitHubAccessToken,
    DeploymentConfigDto Config,
    ProjectMetadataDto Metadata,
    string CsProjectName,
    string RepositoryRoot);

/// <summary>
///     Public receipt returned as soon as a cloud request is admitted.
/// </summary>
/// <param name="RunId">The durable cloud-run identity.</param>
/// <param name="Phase">The recorded cloud-run phase.</param>
/// <param name="QueuedAt">The UTC time at which the request was admitted.</param>
/// <param name="FailureReason">A fixed, safe failure reason, if available.</param>
public sealed record CloudDeploymentReceipt(
    Guid RunId,
    CloudRunPhase Phase,
    DateTimeOffset QueuedAt,
    string? FailureReason);

/// <summary>
///     Authorizes and records SaaS cloud launches without carrying credentials through a queue.
/// </summary>
public interface ICloudDeploymentRunService
{
    /// <summary>
    ///     Creates or returns the request identified by the user's idempotency key.
    /// </summary>
    /// <param name="start">The owner-authorized cloud launch request and stable idempotency key.</param>
    /// <param name="cancellationToken">Cancels the operation when supported by the underlying work.</param>
    /// <returns>A task returning the newly admitted or idempotently reused cloud-run receipt.</returns>
    Task<CloudDeploymentReceipt> StartAsync(CloudDeploymentStart start, CancellationToken cancellationToken = default);

    /// <summary>
    ///     Gets a run only if it belongs to the requesting user.
    /// </summary>
    /// <param name="userId">The internal AutoMate user identity.</param>
    /// <param name="runId">The durable cloud-run identity.</param>
    /// <param name="cancellationToken">Cancels the operation when supported by the underlying work.</param>
    /// <returns>A task returning the owned run receipt, or null when no authorized run matches.</returns>
    Task<CloudDeploymentReceipt?> GetAsync(Guid userId, Guid runId, CancellationToken cancellationToken = default);

    /// <summary>
    ///     Finds the newest run for an authorized project, including its queued age.
    /// </summary>
    /// <param name="userId">The internal AutoMate user identity.</param>
    /// <param name="projectId">The project identity used for authorization or correlation.</param>
    /// <param name="cancellationToken">Cancels the operation when supported by the underlying work.</param>
    /// <returns>A task returning the owned run receipt, or null when no authorized run matches.</returns>
    Task<CloudDeploymentReceipt?> GetLatestForProjectAsync(Guid userId, Guid projectId,
        CancellationToken cancellationToken = default);
}
