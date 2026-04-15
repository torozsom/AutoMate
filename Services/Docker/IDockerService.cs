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
    ///     Builds a Docker image from the specified local source directory.
    ///     It creates a temporary tarball of the directory, streams it to the Docker Engine,
    ///     and waits for the build process to complete.
    /// </summary>
    /// <param name="sourcePath">The local absolute path to the project directory containing the Dockerfile.</param>
    /// <param name="imageTag">The desired tag for the built image (e.g., "automate-myproj:latest").</param>
    Task<bool> BuildImageAsync(string sourcePath, string imageTag);


    /// <summary>
    ///     Creates and starts a Docker container based on an existing image.
    /// </summary>
    /// <param name="imageTag">The tag of the image to run.</param>
    /// <param name="containerName">The name to assign to the new container.</param>
    /// <param name="hostPort">The port on the host machine to expose the application on.</param>
    /// <param name="containerPort">The port the application listens on inside the container (default is 8080 for .NET 8/10).</param>
    /// <param name="envVarsJson">Optional JSON string containing environment variables.</param>
    /// <returns>Returns the unique Container ID if successful, or null if an error occurred.</returns>
    Task<string?> StartContainerAsync(string imageTag, string containerName, int hostPort, int containerPort = 8080,
        string? envVarsJson = null);
}