namespace Application.Abstractions.Docker;

/// <summary>
///     Service interface for managing Docker operations. Provides methods for checking the
///     availability of the Docker daemon and for building and deploying projects as Docker
///     containers.
/// </summary>
public interface IDockerService
{
    /// <summary>
    ///     Checks if the Docker daemon is available and responsive.
    /// </summary>
    /// <param name="cancellationToken">Cancels the operation when supported by the underlying work.</param>
    /// <returns>A task returning whether the Docker daemon is reachable.</returns>
    Task<bool> PingAsync(CancellationToken cancellationToken = default);

    /// <summary>
    ///     Builds a Docker image from a source code directory.
    /// </summary>
    /// <param name="sourcePath">The Docker build-context directory.</param>
    /// <param name="imageTag">The Docker image tag to build or run.</param>
    /// <param name="cancellationToken">Cancels the operation when supported by the underlying work.</param>
    /// <returns>A task returning whether the Docker image build succeeded.</returns>
    Task<bool> BuildImageAsync(string sourcePath, string imageTag, CancellationToken cancellationToken = default);

    /// <summary>
    ///     Starts a Docker container from a specified image, mapping the given host port to the
    ///     container port.
    /// </summary>
    /// <param name="imageTag">The Docker image tag to build or run.</param>
    /// <param name="containerName">The exact Docker container name.</param>
    /// <param name="hostPort">The port exposed on the Docker host.</param>
    /// <param name="containerPort">The application port inside the container.</param>
    /// <param name="envVarsJson">The JSON environment-variable map supplied to the container.</param>
    /// <param name="cancellationToken">Cancels the operation when supported by the underlying work.</param>
    /// <returns>A task returning the started container identity, or null when startup fails.</returns>
    Task<string?> StartContainerAsync(string imageTag, string containerName, int hostPort, int containerPort = 8080,
        string? envVarsJson = null, CancellationToken cancellationToken = default);

    /// <summary>
    ///     Executes the 'docker compose up' command in the specified working directory.
    /// </summary>
    /// <param name="workingDir">The local directory containing the Compose deployment.</param>
    /// <param name="projectName">The project name used for deployment artifacts or Compose lookup.</param>
    /// <param name="projectId">The project identity used for authorization or correlation.</param>
    /// <param name="deploymentId">The exact deployment identity used for ownership or correlation.</param>
    /// <param name="cancellationToken">Cancels the operation when supported by the underlying work.</param>
    /// <returns>A task returning whether Compose up completed successfully.</returns>
    Task<bool> RunDockerComposeUpAsync(string workingDir, string projectName, Guid projectId, Guid deploymentId,
        CancellationToken cancellationToken = default);

    /// <summary>
    ///     Executes the 'docker compose down' command in the specified working directory.
    /// </summary>
    /// <param name="workingDir">The local directory containing the Compose deployment.</param>
    /// <param name="projectName">The project name used for deployment artifacts or Compose lookup.</param>
    /// <param name="projectId">The project identity used for authorization or correlation.</param>
    /// <param name="deploymentId">
    ///     The deployment identity; null is used only where legacy observations permit it.
    /// </param>
    /// <param name="cancellationToken">Cancels the operation when supported by the underlying work.</param>
    /// <returns>A task returning whether Compose down completed successfully.</returns>
    Task<bool> RunDockerComposeDownAsync(string workingDir, string projectName, Guid projectId,
        Guid? deploymentId = null, CancellationToken cancellationToken = default);

    /// <summary>
    ///     Gets a list of all currently running Docker Compose project names.
    /// </summary>
    /// <param name="cancellationToken">Cancels the operation when supported by the underlying work.</param>
    /// <returns>A task returning the discovered running Compose project names.</returns>
    Task<List<string>> GetRunningProjectNamesAsync(CancellationToken cancellationToken = default);

    /// <summary>
    ///     Starts streaming logs for a specified container to the log streamer.
    /// </summary>
    /// <param name="containerName">The exact Docker container name.</param>
    /// <param name="projectId">The project identity used for authorization or correlation.</param>
    /// <param name="deploymentId">The exact deployment identity used for ownership or correlation.</param>
    /// <param name="containerSuffixOrTabId">The stable container terminal suffix used by the browser.</param>
    /// <param name="cancellationToken">Cancels the operation when supported by the underlying work.</param>
    /// <returns>A task representing the container-log subscription until cancellation or termination.</returns>
    Task StreamContainerLogsAsync(string containerName, Guid projectId, Guid deploymentId,
        string containerSuffixOrTabId,
        CancellationToken cancellationToken);

    /// <summary>
    ///     Starts streaming metrics for a specified container to the log streamer.
    /// </summary>
    /// <param name="containerName">The exact Docker container name.</param>
    /// <param name="projectId">The project identity used for authorization or correlation.</param>
    /// <param name="deploymentId">The exact deployment identity used for ownership or correlation.</param>
    /// <param name="containerSuffixOrTabId">The stable container terminal suffix used by the browser.</param>
    /// <param name="cancellationToken">Cancels the operation when supported by the underlying work.</param>
    /// <returns>
    ///     A task representing the container-metric subscription until cancellation or termination.
    /// </returns>
    Task StreamContainerMetricsAsync(string containerName, Guid projectId, Guid deploymentId,
        string containerSuffixOrTabId,
        CancellationToken cancellationToken);

    /// <summary>
    ///     Gets the host port mapped to the specified container.
    /// </summary>
    /// <param name="containerName">The exact Docker container name.</param>
    /// <param name="cancellationToken">Cancels the operation when supported by the underlying work.</param>
    /// <returns>A task returning the discovered host port, or zero when no port is available.</returns>
    Task<int> GetContainerHostPortAsync(string containerName, CancellationToken cancellationToken = default);
}
