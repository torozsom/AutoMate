namespace Services.Docker;

/// <summary>
///     Service interface for managing Docker operations. Provides methods for checking the
///     availability of the Docker daemon and for building and deploying projects as Docker containers.
/// </summary>
public interface IDockerService
{
    /// Checks if the Docker daemon is available and responsive.
    Task<bool> PingAsync();

    /// Builds a Docker image from a source code directory.
    Task<bool> BuildImageAsync(string sourcePath, string imageTag);

    /// Starts a Docker container from a specified image, mapping the given host port to the container port.
    Task<string?> StartContainerAsync(string imageTag, string containerName, int hostPort, int containerPort = 8080,
        string? envVarsJson = null);

    /// Executes the 'docker-compose up' command in the specified working directory.
    Task<bool> RunDockerComposeUpAsync(string workingDir, string projectName, Guid projectId);

    /// Executes the 'docker compose down' command in the specified working directory.
    Task<bool> RunDockerComposeDownAsync(string workingDir, string projectName, Guid projectId);

    /// Gets a list of all currently running Docker Compose project names.
    Task<List<string>> GetRunningProjectNamesAsync();

    /// Starts streaming logs for a specified container to the log streamer.
    Task StreamContainerLogsAsync(string containerName, Guid projectId, string containerSuffixOrTabId,
        CancellationToken cancellationToken);

    /// Starts streaming metrics for a specified container to the log streamer.
    Task StreamContainerMetricsAsync(string containerName, Guid projectId, string containerSuffixOrTabId,
        CancellationToken cancellationToken);
        
    /// Gets the host port mapped to the specified container.
    Task<int> GetContainerHostPortAsync(string containerName);
}