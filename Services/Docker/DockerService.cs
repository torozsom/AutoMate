using System.Runtime.InteropServices;
using Core.Entities;
using Docker.DotNet;
using Services.Data;

namespace Services.Docker;


/// <summary>
///     The <see cref="DockerService"/> class provides functionality to interact with the Docker daemon.
///     It allows checking the availability of the Docker service and is designed to handle Docker-related
///     operations, such as building and deploying projects. The service is initialized with a database context,
///     which can be used for any necessary interactions with the database related to Docker operations. The class
///     also implements the IDisposable interface to ensure proper resource management when working with the Docker client.
/// </summary>
public class DockerService : IDockerService, IDisposable
{
    private readonly DockerClient _client;
    private readonly AutoMateDbContext _dbContext;


    /// <summary>
    ///     Initializes a new instance of the <see cref="DockerService"/> class. It sets up
    ///     the Docker client configuration based on the operating system. For Windows, it
    ///     uses a named pipe to connect to the Docker daemon, while for Unix-based systems,
    ///     it uses a Unix socket. The constructor also accepts a database context for potential
    ///     interactions with the database related to Docker operations.
    /// </summary>
    /// <param name="dbContext">Database context for Docker operations.</param>
    public DockerService(AutoMateDbContext dbContext)
    {
        _dbContext = dbContext;

        var dockerUri = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? new Uri("npipe://./pipe/docker_engine")
            : new Uri("unix:///var/run/docker.sock");

        _client = new DockerClientConfiguration(dockerUri).CreateClient();
    }


    /// <summary>
    ///     Checks if the Docker daemon is running and accessible by sending a ping request.
    ///     If the ping is successful, it returns true; otherwise, it catches any exceptions
    ///     and returns false, indicating that the Docker daemon is not available.
    /// </summary>
    /// <returns>Returns true if Docker daemon is available, false otherwise.</returns>
    public async Task<bool> PingAsync()
    {
        try
        {
            await _client.System.PingAsync();
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }


    /// <summary>
    ///     Disposes of the Docker client resources when the service is no longer needed.
    ///     This method is important for releasing any unmanaged resources held by the
    ///     Docker client and ensuring that connections to the Docker daemon are properly closed.
    ///     By implementing the IDisposable interface, this service can be used in a using
    ///     statement or disposed of manually to free up resources efficiently.
    /// </summary>
    public void Dispose() =>_client.Dispose();


    public async Task<Deployment?> BuildAndDeployLocalProjectAsync(Project project)
    {
        throw new NotImplementedException();
    }
}