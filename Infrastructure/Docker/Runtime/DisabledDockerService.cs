using Application.Abstractions.Docker;

namespace Infrastructure.Docker;

/// <summary>
///     Safe placeholder used by hosted SaaS instances. It prevents any Docker daemon
///     interaction while keeping pages that display historical local deployments constructible.
/// </summary>
public sealed class DisabledDockerService : IDockerService
{
    /// <summary>
    ///     Checks if the Docker daemon is available and responsive.
    /// </summary>
    /// <param name="cancellationToken">Cancels the operation when supported by the underlying work.</param>
    /// <returns>A task returning whether the Docker daemon is reachable.</returns>
    public Task<bool> PingAsync(CancellationToken cancellationToken = default)
    {
        return Task.FromResult(false);
    }

    /// <summary>
    ///     Builds a Docker image from a source code directory.
    /// </summary>
    /// <param name="sourcePath">The Docker build-context directory.</param>
    /// <param name="imageTag">The Docker image tag to build or run.</param>
    /// <param name="cancellationToken">Cancels the operation when supported by the underlying work.</param>
    /// <returns>A faulted task reporting the disabled Docker capability.</returns>
    /// <exception cref="InvalidOperationException">
    ///     Docker operations are disabled by the selected hosting profile.
    /// </exception>
    public Task<bool> BuildImageAsync(string sourcePath, string imageTag, CancellationToken cancellationToken = default)
    {
        return Task.FromException<bool>(Disabled());
    }

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
    /// <returns>A faulted task reporting the disabled Docker capability.</returns>
    /// <exception cref="InvalidOperationException">
    ///     Docker operations are disabled by the selected hosting profile.
    /// </exception>
    public Task<string?> StartContainerAsync(string imageTag, string containerName, int hostPort,
        int containerPort = 8080,
        string? envVarsJson = null, CancellationToken cancellationToken = default)
    {
        return Task.FromException<string?>(Disabled());
    }

    /// <summary>
    ///     Executes the 'docker compose up' command in the specified working directory.
    /// </summary>
    /// <param name="workingDir">The local directory containing the Compose deployment.</param>
    /// <param name="projectName">The project name used for deployment artifacts or Compose lookup.</param>
    /// <param name="projectId">The project identity used for authorization or correlation.</param>
    /// <param name="deploymentId">The exact deployment identity used for ownership or correlation.</param>
    /// <param name="cancellationToken">Cancels the operation when supported by the underlying work.</param>
    /// <returns>A faulted task reporting the disabled Docker capability.</returns>
    /// <exception cref="InvalidOperationException">
    ///     Docker operations are disabled by the selected hosting profile.
    /// </exception>
    public Task<bool> RunDockerComposeUpAsync(string workingDir, string projectName, Guid projectId, Guid deploymentId,
        CancellationToken cancellationToken = default)
    {
        return Task.FromException<bool>(Disabled());
    }

    /// <summary>
    ///     Executes the 'docker compose down' command in the specified working directory.
    /// </summary>
    /// <param name="workingDir">The local directory containing the Compose deployment.</param>
    /// <param name="projectName">The project name used for deployment artifacts or Compose lookup.</param>
    /// <param name="projectId">The project identity used for authorization or correlation.</param>
    /// <param name="cancellationToken">Cancels the operation when supported by the underlying work.</param>
    /// <param name="deploymentId">
    ///     The deployment identity; null is used only where legacy observations permit it.
    /// </param>
    /// <returns>A faulted task reporting the disabled Docker capability.</returns>
    /// <exception cref="InvalidOperationException">
    ///     Docker operations are disabled by the selected hosting profile.
    /// </exception>
    public Task<bool> RunDockerComposeDownAsync(string workingDir, string projectName, Guid projectId,
        CancellationToken cancellationToken = default, Guid? deploymentId = null)
    {
        return Task.FromException<bool>(Disabled());
    }

    /// <summary>
    ///     Gets a list of all currently running Docker Compose project names.
    /// </summary>
    /// <param name="cancellationToken">Cancels the operation when supported by the underlying work.</param>
    /// <returns>A task returning the discovered running Compose project names.</returns>
    public Task<List<string>> GetRunningProjectNamesAsync(CancellationToken cancellationToken = default)
    {
        return Task.FromResult(new List<string>());
    }

    /// <summary>
    ///     Starts streaming logs for a specified container to the log streamer.
    /// </summary>
    /// <param name="containerName">The exact Docker container name.</param>
    /// <param name="projectId">The project identity used for authorization or correlation.</param>
    /// <param name="deploymentId">The exact deployment identity used for ownership or correlation.</param>
    /// <param name="containerSuffixOrTabId">The stable container terminal suffix used by the browser.</param>
    /// <param name="cancellationToken">Cancels the operation when supported by the underlying work.</param>
    /// <returns>A faulted task reporting the disabled Docker capability.</returns>
    /// <exception cref="InvalidOperationException">
    ///     Docker operations are disabled by the selected hosting profile.
    /// </exception>
    public Task StreamContainerLogsAsync(string containerName, Guid projectId, Guid deploymentId,
        string containerSuffixOrTabId,
        CancellationToken cancellationToken)
    {
        return Task.FromException(Disabled());
    }

    /// <summary>
    ///     Starts streaming metrics for a specified container to the log streamer.
    /// </summary>
    /// <param name="containerName">The exact Docker container name.</param>
    /// <param name="projectId">The project identity used for authorization or correlation.</param>
    /// <param name="deploymentId">The exact deployment identity used for ownership or correlation.</param>
    /// <param name="containerSuffixOrTabId">The stable container terminal suffix used by the browser.</param>
    /// <param name="cancellationToken">Cancels the operation when supported by the underlying work.</param>
    /// <returns>A faulted task reporting the disabled Docker capability.</returns>
    /// <exception cref="InvalidOperationException">
    ///     Docker operations are disabled by the selected hosting profile.
    /// </exception>
    public Task StreamContainerMetricsAsync(string containerName, Guid projectId, Guid deploymentId,
        string containerSuffixOrTabId,
        CancellationToken cancellationToken)
    {
        return Task.FromException(Disabled());
    }

    /// <summary>
    ///     Gets the host port mapped to the specified container.
    /// </summary>
    /// <param name="containerName">The exact Docker container name.</param>
    /// <param name="cancellationToken">Cancels the operation when supported by the underlying work.</param>
    /// <returns>A faulted task reporting the disabled Docker capability.</returns>
    /// <exception cref="InvalidOperationException">
    ///     Docker operations are disabled by the selected hosting profile.
    /// </exception>
    public Task<int> GetContainerHostPortAsync(string containerName, CancellationToken cancellationToken = default)
    {
        return Task.FromException<int>(Disabled());
    }

    /// <summary>
    ///     Creates fixed guidance for Docker operations disabled by the hosting profile.
    /// </summary>
    /// <returns>A fixed exception explaining that Docker capability is disabled.</returns>
    private static InvalidOperationException Disabled()
    {
        return new InvalidOperationException("The Docker daemon is not available in this AutoMate hosting profile.");
    }
}
