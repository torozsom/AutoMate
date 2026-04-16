using System.Formats.Tar;
using System.Runtime.InteropServices;
using System.Text.Json;
using Docker.DotNet;
using Docker.DotNet.Models;

namespace Services.Docker;

/// <summary>
///     The <see cref="DockerService" /> class provides functionality to interact with the Docker daemon.
///     It allows checking the availability of the Docker service and is designed to handle Docker-related
///     operations, such as building and deploying projects. The class also implements the IDisposable interface
///     to ensure proper resource management when working with the Docker client.
/// </summary>
public class DockerService : IDockerService, IDisposable
{
    private readonly DockerClient _client;


    /// <summary>
    ///     Initializes a new instance of the <see cref="DockerService" /> class. It sets up
    ///     the Docker client configuration based on the operating system. For Windows, it
    ///     uses a named pipe to connect to the Docker daemon, while for Unix-based systems,
    ///     it uses a Unix socket.
    /// </summary>
    public DockerService()
    {
        var dockerUri = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? new Uri("npipe://./pipe/docker_engine")
            : new Uri("unix:///var/run/docker.sock");

        _client = new DockerClientConfiguration(dockerUri).CreateClient();
    }


    /// <summary>
    ///     Disposes of the Docker client resources when the service is no longer needed.
    ///     This method is important for releasing any unmanaged resources held by the
    ///     Docker client and ensuring that connections to the Docker daemon are properly closed.
    ///     By implementing the IDisposable interface, this service can be used in a using
    ///     statement or disposed of manually to free up resources efficiently.
    /// </summary>
    public void Dispose()
    {
        _client.Dispose();
    }


    /// <summary>
    ///     Checks if the Docker daemon is available and responsive by sending a ping request.
    /// </summary>
    /// <returns>A boolean indicating if the Docker daemon is available.</returns>
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
    ///     Builds a Docker image from a specified source directory and tags it with the provided image tag.
    /// </summary>
    /// <param name="sourcePath">The path to the source directory containing the Dockerfile and associated build context.</param>
    /// <param name="imageTag">The tag to assign to the built Docker image.</param>
    /// <returns>
    ///     A task representing the asynchronous operation, returning true if the image was successfully built,
    ///     otherwise false.
    /// </returns>
    public async Task<bool> BuildImageAsync(string sourcePath, string imageTag)
    {
        var tempTarFilePath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid()}.tar");

        try
        {
            await TarFile.CreateFromDirectoryAsync(sourcePath, tempTarFilePath, false);
            await using var fileStream = new FileStream(tempTarFilePath, FileMode.Open, FileAccess.Read);

            var buildParameters = new ImageBuildParameters { Tags = [imageTag] };

            var buildErrorOccurred = false;
            await _client.Images.BuildImageFromDockerfileAsync(
                buildParameters,
                fileStream,
                null,
                null,
                new Progress<JSONMessage>(msg =>
                {
                    if (!string.IsNullOrEmpty(msg.Stream))
                        Console.Write(msg.Stream);

                    if (!string.IsNullOrEmpty(msg.ErrorMessage))
                    {
                        Console.WriteLine($"[DOCKER ERROR]: {msg.ErrorMessage}");
                        buildErrorOccurred = true;
                    }
                }));
            return !buildErrorOccurred;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error building image: {ex.Message}");
            return false;
        }
        finally
        {
            if (File.Exists(tempTarFilePath))
                File.Delete(tempTarFilePath);
        }
    }


    /// <summary>
    ///     Starts a Docker container asynchronously based on the provided configuration parameters.
    ///     This includes specifying the image, container name, ports, and optional environment variables.
    ///     Returns the ID of the started container if successful, or null if the operation fails.
    /// </summary>
    /// <param name="imageTag">The tag of the Docker image to use for the container.</param>
    /// <param name="containerName">The name to assign to the container.</param>
    /// <param name="hostPort">The port on the host machine to bind to the container's port.</param>
    /// <param name="containerPort">The port within the container to be exposed, with a default value of 8080.</param>
    /// <param name="envVarsJson">Optional JSON string specifying environment variables to pass to the container.</param>
    /// <returns>The ID of the started container if the operation is successful, or null if it fails.</returns>
    public async Task<string?> StartContainerAsync(string imageTag, string containerName, int hostPort,
        int containerPort = 8080,
        string? envVarsJson = null)
    {
        try
        {
            var envList = new List<string>();
            if (!string.IsNullOrEmpty(envVarsJson))
            {
                var envDict = JsonSerializer.Deserialize<Dictionary<string, string>>(envVarsJson);
                if (envDict != null)
                    envList.AddRange(envDict.Select(kv => $"{kv.Key}={kv.Value}"));
            }

            var createParams = new CreateContainerParameters
            {
                Image = imageTag,
                Name = containerName,
                Env = envList,

                ExposedPorts = new Dictionary<string, EmptyStruct>
                {
                    { $"{containerPort}/tcp", default }
                },

                HostConfig = new HostConfig
                {
                    PortBindings = new Dictionary<string, IList<PortBinding>>
                    {
                        {
                            $"{containerPort}/tcp",
                            [new PortBinding { HostPort = hostPort.ToString() }]
                        }
                    }
                }
            };

            var response = await _client.Containers.CreateContainerAsync(createParams);
            var containerId = response.ID;
            var started = await _client.Containers.StartContainerAsync(containerId, new ContainerStartParameters());
            return started ? containerId : null;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error starting container: {ex.Message}");
            return null;
        }
    }
}