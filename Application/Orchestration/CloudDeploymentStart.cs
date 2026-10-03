using Domain.DTO;
using Domain.Enums;

namespace Application.Orchestration;

/// <summary>Inputs for an authorized, durable SaaS cloud deployment request.</summary>
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

/// <summary>Public receipt returned as soon as a cloud request is admitted.</summary>
public sealed record CloudDeploymentReceipt(Guid RunId, CloudRunPhase Phase, DateTimeOffset QueuedAt,
    string? FailureReason);

/// <summary>Authorizes and records SaaS cloud launches without carrying credentials through a queue.</summary>
public interface ICloudDeploymentRunService
{
    /// <summary>Creates or returns the request identified by the user's idempotency key.</summary>
    Task<CloudDeploymentReceipt> StartAsync(CloudDeploymentStart start, CancellationToken cancellationToken = default);

    /// <summary>Gets a run only if it belongs to the requesting user.</summary>
    Task<CloudDeploymentReceipt?> GetAsync(Guid userId, Guid runId, CancellationToken cancellationToken = default);

    /// <summary>Finds the newest run for an authorized project, including its queued age.</summary>
    Task<CloudDeploymentReceipt?> GetLatestForProjectAsync(Guid userId, Guid projectId,
        CancellationToken cancellationToken = default);
}
