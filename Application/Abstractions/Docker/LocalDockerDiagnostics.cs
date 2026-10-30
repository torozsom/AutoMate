namespace Application.Abstractions.Docker;

/// <summary>
///     Identifies a known project container and its existing terminal tab.
/// </summary>
/// <param name="Name">The recorded display name or fixed metric identity.</param>
/// <param name="Channel">The stable terminal channel used for routing.</param>
/// <param name="IsDatabase">Whether the owned container is a database service.</param>
public sealed record DockerContainerTarget(string Name, string Channel, bool IsDatabase = false);

/// <summary>
///     Non-secret ownership and correlation information for a local deployment collector.
/// </summary>
/// <param name="ProjectId">The project identity used for authorization or correlation.</param>
/// <param name="DeploymentId">The exact deployment identity used for ownership or correlation.</param>
/// <param name="ComposeProject">
///     The normalized Compose project identity used to verify owned Docker resources.
/// </param>
/// <param name="Containers">The immutable inventory of verified owned web and database containers.</param>
/// <param name="RegisteredAt">The UTC time at which the collector inventory was registered.</param>
public sealed record DockerDeploymentTarget(
    Guid ProjectId,
    Guid DeploymentId,
    string ComposeProject,
    IReadOnlyList<DockerContainerTarget> Containers,
    DateTimeOffset RegisteredAt);

/// <summary>
///     Controls host-owned local collectors without exposing Docker SDK types.
/// </summary>
public interface ILocalDeploymentDiagnostics
{
    /// <summary>
    ///     Registers or replaces a deployment; startup operations collect lifecycle events before
    ///     Compose runs.
    /// </summary>
    /// <param name="target">The verified project/deployment collector inventory.</param>
    /// <param name="deploymentOperation">
    ///     Whether a deployment operation currently requires daemon collection.
    /// </param>
    /// <param name="cancellationToken">Cancels the operation when supported by the underlying work.</param>
    /// <returns>A task that completes after the deployment's diagnostic supervisor is registered.</returns>
    Task RegisterAsync(DockerDeploymentTarget target, bool deploymentOperation,
        CancellationToken cancellationToken = default);

    /// <summary>
    ///     Checks whether recovery must register a current deployment.
    /// </summary>
    /// <param name="projectId">The project identity used for authorization or correlation.</param>
    /// <param name="deploymentId">The exact deployment identity used for ownership or correlation.</param>
    /// <returns>Whether a matching deployment supervisor is registered.</returns>
    bool IsActive(Guid projectId, Guid deploymentId);

    /// <summary>
    ///     Enables lifecycle collection for a deployment operation, independently of runtime
    ///     viewing consent.
    /// </summary>
    /// <param name="projectId">The project identity used for authorization or correlation.</param>
    /// <param name="deploymentId">The exact deployment identity used for ownership or correlation.</param>
    /// <param name="active">The current active-operation count.</param>
    /// <param name="cancellationToken">Cancels the operation when supported by the underlying work.</param>
    /// <returns>
    ///     A task that completes after the supervisor's deployment-operation interest is updated.
    /// </returns>
    Task SetOperationAsync(Guid projectId, Guid deploymentId, bool active,
        CancellationToken cancellationToken = default);

    /// <summary>
    ///     Quiesces container logs and metrics before removal while preserving daemon lifecycle collection.
    /// </summary>
    /// <param name="projectId">Project whose owned runtime sources must stop.</param>
    /// <param name="deploymentId">Exact deployment identity; stale requests have no effect.</param>
    /// <param name="cancellationToken">Cancels waiting for source shutdown.</param>
    /// <returns>Completes after runtime sources acknowledge cancellation.</returns>
    Task StopRuntimeAsync(Guid projectId, Guid deploymentId, CancellationToken cancellationToken = default);

    /// <summary>
    ///     Cancels and awaits all collectors for a project.
    /// </summary>
    /// <param name="projectId">The project identity used for authorization or correlation.</param>
    /// <param name="cancellationToken">Cancels the operation when supported by the underlying work.</param>
    /// <returns>A task that completes after the project's supervised sources have stopped.</returns>
    Task StopProjectAsync(Guid projectId, CancellationToken cancellationToken = default);
}

/// <summary>
///     Provider boundary for label-filtered daemon events and incremental container output.
/// </summary>
public interface IDockerDiagnosticSource
{
    /// <summary>
    ///     Monitors daemon events; signals the first subscription attempt so deployment can proceed
    ///     safely.
    /// </summary>
    /// <param name="target">The verified project/deployment collector inventory.</param>
    /// <param name="subscribed">Called after the diagnostic source has established its subscription.</param>
    /// <param name="cancellationToken">Cancels the operation when supported by the underlying work.</param>
    /// <returns>A task representing daemon monitoring until cancellation or source termination.</returns>
    Task MonitorDaemonAsync(DockerDeploymentTarget target, Action subscribed, CancellationToken cancellationToken);

    /// <summary>
    ///     Streams a verified project's container with bounded parsing, replay and reconnect
    ///     handling.
    /// </summary>
    /// <param name="target">The verified project/deployment collector inventory.</param>
    /// <param name="container">The verified owned Docker container target.</param>
    /// <param name="cancellationToken">Cancels the operation when supported by the underlying work.</param>
    /// <returns>A task representing owned-container log monitoring until cancellation or termination.</returns>
    Task MonitorContainerAsync(DockerDeploymentTarget target, DockerContainerTarget container,
        CancellationToken cancellationToken);

    /// <summary>
    ///     Samples a verified container while preserving the independent live/history metric
    ///     cadence.
    /// </summary>
    /// <param name="target">The verified project/deployment collector inventory.</param>
    /// <param name="container">The verified owned Docker container target.</param>
    /// <param name="cancellationToken">Cancels the operation when supported by the underlying work.</param>
    /// <returns>
    ///     A task representing owned-container metric monitoring until cancellation or termination.
    /// </returns>
    Task MonitorMetricsAsync(DockerDeploymentTarget target, DockerContainerTarget container,
        CancellationToken cancellationToken);
}
