namespace Services.Docker;

/// <summary>
///     Service interface for managing Docker operations.
///     Provides methods for checking the availability of the Docker daemon
///     and for building and deploying projects as Docker containers.
/// </summary>
public interface IDockerService
{
    /// <summary>Checks if the Docker daemon is available and responsive.</summary>
    Task<bool> PingAsync(CancellationToken cancellationToken = default);

    /// <summary>Builds a Docker image from a source code directory.</summary>
    Task<bool> BuildImageAsync(string sourcePath, string imageTag, CancellationToken cancellationToken = default);

    /// <summary>Starts a Docker container from a specified image, mapping the given host port to the container port.</summary>
    Task<string?> StartContainerAsync(string imageTag, string containerName, int hostPort, int containerPort = 8080,
        string? envVarsJson = null, CancellationToken cancellationToken = default);

    /// <summary>Executes the 'docker compose up' command in the specified working directory.</summary>
    Task<bool> RunDockerComposeUpAsync(string workingDir, string projectName, Guid projectId,
        CancellationToken cancellationToken = default);

    /// <summary>Executes the 'docker compose down' command in the specified working directory.</summary>
    Task<bool> RunDockerComposeDownAsync(string workingDir, string projectName, Guid projectId,
        CancellationToken cancellationToken = default);

    /// <summary>Gets a list of all currently running Docker Compose project names.</summary>
    Task<List<string>> GetRunningProjectNamesAsync(CancellationToken cancellationToken = default);

    /// <summary>Starts streaming logs for a specified container to the log streamer.</summary>
    Task StreamContainerLogsAsync(string containerName, Guid projectId, string containerSuffixOrTabId,
        CancellationToken cancellationToken);

    /// <summary>Starts streaming metrics for a specified container to the log streamer.</summary>
    Task StreamContainerMetricsAsync(string containerName, Guid projectId, string containerSuffixOrTabId,
        CancellationToken cancellationToken);

    /// <summary>Gets the host port mapped to the specified container.</summary>
    Task<int> GetContainerHostPortAsync(string containerName, CancellationToken cancellationToken = default);
}