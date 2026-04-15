using Core.Entities;

namespace Services.Docker;

/// <summary>
///     Service interface for managing Docker operations. Provides methods for checking the
///     availability of the Docker daemon and for building and deploying projects as Docker containers.
/// </summary>
public interface IDockerService
{
    /// <summary>
    ///     Checks if the Docker daemon is running and accessible by sending a ping request.
    /// </summary>
    /// <returns>A boolean indicator if the daemon is available.</returns>
    Task<bool> PingAsync();

    /// <summary>
    ///     Builds a Docker image from the specified project and deploys it as a container.
    ///     The method returns the deployment information if the build and deployment process
    ///     is successful, or null if it fails.
    /// </summary>
    /// <param name="project">The project to deploy.</param>
    /// <returns>Returns deployment information if successful, null otherwise.</returns>
    Task<Deployment?> BuildAndDeployLocalProjectAsync(Project project);
}