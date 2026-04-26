using System.Diagnostics;
using System.Formats.Tar;
using System.Runtime.InteropServices;
using System.Text.Json;
using Docker.DotNet;
using Docker.DotNet.Models;
using Microsoft.Extensions.Logging;

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
    private readonly ILogger<DockerService> _logger;


    /// <summary>
    ///     Initializes a new instance of the <see cref="DockerService" /> class. It sets up
    ///     the Docker client configuration based on the operating system. For Windows, it
    ///     uses a named pipe to connect to the Docker daemon, while for Unix-based systems,
    ///     it uses a Unix socket.
    /// </summary>
    public DockerService(ILogger<DockerService> logger)
    {
        _logger = logger;

        var dockerUri = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? new Uri("npipe://./pipe/docker_engine")
            : new Uri("unix:///var/run/docker.sock");

        _client = new DockerClientConfiguration(dockerUri).CreateClient();
    }

    /// <summary>
    ///     Disposes of the resources used by the DockerService instance.
    ///     This method ensures that the Docker client is properly disposed of,
    /// </summary>
    public void Dispose()
    {
        _client.Dispose();
        GC.SuppressFinalize(this);
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
            _logger.LogInformation("Building Docker image from source directory: {SourcePath}", sourcePath);

            // Create a tar context from the source directory
            await CreateTarContextAsync(sourcePath, tempTarFilePath);
            await using var fileStream = new FileStream(tempTarFilePath, FileMode.Open, FileAccess.Read);
            var buildParameters = new ImageBuildParameters { Tags = [imageTag] };

            var buildErrorOccurred = false;

            _logger.LogInformation("Starting Docker build for image tag: {ImageTag}", imageTag);

            // Build the image and capture the output messages to log progress and errors
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
                        _logger.LogError("[DOCKER ERROR]: {ObjErrorMessage}", msg.ErrorMessage);
                        buildErrorOccurred = true;
                    }
                }));

            if (!buildErrorOccurred)
                _logger.LogInformation("Docker image '{ImageTag}' built successfully.", imageTag);

            return !buildErrorOccurred;
        }
        catch (Exception ex)
        {
            _logger.LogError("Error building image: {ExMessage}", ex.Message);
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
            _logger.LogInformation("Starting container with image tag: {ImageTag}, " +
                                   "container name: {ContainerName}, " +
                                   "host port: {HostPort}, " +
                                   "container port: {ContainerPort}",
                imageTag, containerName, hostPort, containerPort);

            var envList = new List<string>();
            if (!string.IsNullOrEmpty(envVarsJson))
            {
                _logger.LogInformation("Starting container with environment variables: {EnvVarsJson}", envVarsJson);
                var envDict = JsonSerializer.Deserialize<Dictionary<string, string>>(envVarsJson);
                if (envDict != null)
                    envList.AddRange(envDict.Select(kv => $"{kv.Key}={kv.Value}"));
            }

            // Configure the container creation parameters
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

            // Start the container
            var response = await _client.Containers.CreateContainerAsync(createParams);
            var containerId = response.ID;
            var started = await _client.Containers.StartContainerAsync(containerId, new ContainerStartParameters());

            _logger.LogInformation("Container '{ContainerId}' started successfully.", containerId);
            return started ? containerId : null;
        }
        catch (Exception ex)
        {
            _logger.LogError("Error starting container: {ExMessage}", ex.Message);
            return null;
        }
    }


    /// <summary>
    ///     Executes the 'docker compose up -d' command in a specified working directory with a given project name.
    /// </summary>
    /// <param name="workingDir">The working directory where the docker command should be run.</param>
    /// <param name="projectName">The name of the project to be containerized.</param>
    /// <returns></returns>
    public async Task<bool> RunDockerComposeUpAsync(string workingDir, string projectName)
    {
        var safeProjectName = projectName.ToLowerInvariant().Replace(" ", "-").Replace(".", "-");

        _logger.LogInformation("Starting 'docker compose up -d' in {Directory}", workingDir);

        var startInfo = new ProcessStartInfo
        {
            FileName = "docker",
            Arguments = $"compose -p {safeProjectName} up -d --build",
            WorkingDirectory = workingDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var process = new Process();
        process.StartInfo = startInfo;

        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data != null) _logger.LogInformation("[Docker STDOUT]: {Data}", e.Data);
        };
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data != null) _logger.LogWarning("[Docker STDERR]: {Data}", e.Data);
        };

        try
        {
            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(8));
            await process.WaitForExitAsync(cts.Token);

            if (process.ExitCode != 0)
            {
                _logger.LogError("Docker Compose failed with exit code {Code}", process.ExitCode);
                return false;
            }

            _logger.LogInformation("Docker Compose started successfully.");
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogCritical(ex, "Critical error while launching Docker process.");
            return false;
        }
    }


    /// <summary>
    ///     Creates a tar context by packaging the specified source directory into a tar file.
    ///     The method respects the .dockerignore file, if present, to exclude certain files and directories
    ///     from being included in the tar. If no .dockerignore file exists, a default set of ignored paths is used.
    /// </summary>
    /// <param name="sourceDirectory">The source directory containing the files to be packaged into the tar file.</param>
    /// <param name="targetTarFilePath">The file path where the created tar file will be saved.</param>
    private static async Task CreateTarContextAsync(string sourceDirectory, string targetTarFilePath)
    {
        if (!Directory.Exists(sourceDirectory))
            throw new DirectoryNotFoundException($"Source directory '{sourceDirectory}' does not exist.");

        // Initilize the ignore rules
        var ignore = new Ignore.Ignore();
        var dockerIgnorePath = Path.Combine(sourceDirectory, ".dockerignore");

        if (File.Exists(dockerIgnorePath))
        {
            var lines = await File.ReadAllLinesAsync(dockerIgnorePath);
            ignore.Add(lines);
        }
        else
        {
            ignore.Add(["bin/", "obj/", ".git/", ".vs/"]);
        }

        // Create the tar file
        await using var fileStream = new FileStream(targetTarFilePath, FileMode.Create, FileAccess.Write);
        await using var tarWriter = new TarWriter(fileStream);

        // Add all files to the tar respecting the ignore rules
        var allFiles = Directory.GetFiles(sourceDirectory, "*.*", SearchOption.AllDirectories);
        foreach (var filePath in allFiles)
        {
            var relativePath = Path.GetRelativePath(sourceDirectory, filePath).Replace('\\', '/');
            if (!ignore.IsIgnored(relativePath))
                await tarWriter.WriteEntryAsync(filePath, relativePath);
        }
    }
}