using System.Buffers;
using System.Diagnostics;
using System.Formats.Tar;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Docker.DotNet;
using Docker.DotNet.Models;
using Microsoft.Extensions.Logging;
using Services.LogStreaming;

namespace Services.Docker;

/// <summary>
///     The <see cref="DockerService" /> class provides functionality to interact with the Docker daemon.
///     It allows checking the availability of the Docker service and is designed to handle Docker-related
///     operations, such as building and deploying projects. The class also implements the IDisposable interface
///     to ensure proper resource management when working with the Docker client.
/// </summary>
public class DockerService : IDockerService, IDisposable
{
    private const int DefaultContainerPort = 8080;
    private const int DockerComposeTimeoutMinutes = 8;

    private const string WindowsDockerUri = "npipe://./pipe/docker_engine";
    private const string UnixDockerUri = "unix:///var/run/docker.sock";

    private readonly DockerClient _client;
    private readonly ILogger<DockerService> _logger;
    private readonly ILogStreamer _logStreamer;


    /// <summary>
    ///     Initializes a new instance of the <see cref="DockerService" /> class. It sets up
    ///     the Docker client configuration based on the operating system. For Windows, it
    ///     uses a named pipe to connect to the Docker daemon, while for Unix-based systems,
    ///     it uses a Unix socket.
    /// </summary>
    public DockerService(ILogger<DockerService> logger, ILogStreamer logStreamer)
    {
        _logger = logger;
        _logStreamer = logStreamer;

        var dockerUri = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? new Uri(WindowsDockerUri)
            : new Uri(UnixDockerUri);

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
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[DockerService] Docker daemon is not responsive during ping.");
            return false;
        }
    }


    /// <summary>
    ///     Builds a Docker image from a specified source directory and tags it with the provided image tag.
    /// </summary>
    /// <param name="sourcePath">
    ///     The path to the source directory containing the Dockerfile and associated build context.
    /// </param>
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
            _logger.LogInformation("[DockerService] Building Docker image '{ImageTag}' " +
                                   "from source directory: {SourcePath}", imageTag, sourcePath);

            // Create a tar context from the source directory
            await CreateTarContextAsync(sourcePath, tempTarFilePath);

            await using var fileStream = new FileStream(tempTarFilePath, FileMode.Open, FileAccess.Read);
            var buildParameters = new ImageBuildParameters { Tags = [imageTag] };
            var buildErrorOccurred = false;

            await _client.Images.BuildImageFromDockerfileAsync(
                buildParameters,
                fileStream,
                null,
                null,
                new Progress<JSONMessage>(msg => HandleDockerBuildProgress(msg, ref buildErrorOccurred)));

            if (!buildErrorOccurred)
                _logger.LogInformation("[DockerService] Docker image '{ImageTag}' built successfully.", imageTag);

            return !buildErrorOccurred;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[DockerService] Error building Docker image '{ImageTag}'.", imageTag);
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
    public async Task<string?> StartContainerAsync(
        string imageTag,
        string containerName,
        int hostPort,
        int containerPort = DefaultContainerPort,
        string? envVarsJson = null)
    {
        try
        {
            _logger.LogInformation("[DockerService] Starting container '{ContainerName}' " +
                                   "(Image: {ImageTag}, Port: {HostPort}->{ContainerPort})",
                containerName, imageTag, hostPort, containerPort);

            var createParams
                = BuildContainerParameters(imageTag, containerName, hostPort, containerPort, envVarsJson);

            var response = await _client.Containers.CreateContainerAsync(createParams);
            var containerId = response.ID;
            var started = await _client.Containers.StartContainerAsync(containerId, new ContainerStartParameters());

            if (started)
            {
                _logger.LogInformation(
                    "[DockerService] Container '{ContainerName}' ({ContainerId}) started successfully.",
                    containerName, containerId[..8]);
                return containerId;
            }

            _logger.LogWarning("[DockerService] Container '{ContainerName}' was created but failed to start.",
                containerName);
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[DockerService] Error starting container '{ContainerName}'.", containerName);
            return null;
        }
    }


    /// <summary>
    ///     Executes the 'docker compose up -d' command in a specified working directory with a given project name.
    /// </summary>
    /// <param name="workingDir">The working directory where the docker command should be run.</param>
    /// <param name="projectName">The name of the project to be containerized.</param>
    /// <param name="projectId">The ID of the project.</param>
    /// <returns>Returns an indicator if the docker compose up command was successfully run.</returns>
    public async Task<bool> RunDockerComposeUpAsync(string workingDir, string projectName, Guid projectId)
    {
        var safeProjectName = NormalizeProjectName(projectName);
        _logger.LogInformation("[DockerService] Starting 'docker compose up -d' for project '{ProjectName}' " +
                               "in {Directory}", safeProjectName, workingDir);

        var startInfo = CreateDockerComposeStartInfo(workingDir, safeProjectName);

        using var process = new Process();
        process.StartInfo = startInfo;

        process.OutputDataReceived += (_, e) =>
        {
            if (!string.IsNullOrWhiteSpace(e.Data))
                _logger.LogDebug("[Docker Compose]: {Data}", e.Data);

            _ = _logStreamer.StreamBuildLogsAsync(projectId, e.Data + "\r\n");
        };

        process.ErrorDataReceived += (_, e) =>
        {
            if (!string.IsNullOrWhiteSpace(e.Data))
                _logger.LogWarning("[Docker Compose]: {Data}", e.Data);

            _ = _logStreamer.StreamBuildLogsAsync(projectId, e.Data + "\r\n");
        };

        try
        {
            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(DockerComposeTimeoutMinutes));
            await process.WaitForExitAsync(cts.Token);

            if (process.ExitCode != 0)
            {
                _logger.LogError(
                    "[DockerService] Docker Compose failed for project '{ProjectName}' with exit code {Code}.",
                    safeProjectName, process.ExitCode);
                return false;
            }

            _logger.LogInformation("[DockerService] Docker Compose completed successfully for project '{ProjectName}'.",
                safeProjectName);
            return true;
        }
        catch (OperationCanceledException)
        {
            _logger.LogError(
                "[DockerService] Docker Compose timed out after {Timeout} minutes for project '{ProjectName}'.",
                DockerComposeTimeoutMinutes, safeProjectName);
            if (!process.HasExited) process.Kill();
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogCritical(ex, "[DockerService] Critical error while launching " +
                                    "Docker Compose process for '{ProjectName}'.", safeProjectName);
            return false;
        }
    }


    /// <summary>
    ///     Executes the 'docker compose down' command in a specified working directory with a given project name.
    /// </summary>
    /// <param name="workingDir">The working directory where the docker command should be run.</param>
    /// <param name="projectName">The name of the project whose containers should be stopped.</param>
    /// <param name="projectId">The ID of the project.</param>
    /// <returns>Returns an indicator if the docker compose down command was successfully run.</returns>
    public async Task<bool> RunDockerComposeDownAsync(string workingDir, string projectName, Guid projectId)
    {
        var safeProjectName = NormalizeProjectName(projectName);
        _logger.LogInformation("[DockerService] Starting 'docker compose down' for project '{ProjectName}' " +
                               "in {Directory}", safeProjectName, workingDir);

        var startInfo = new ProcessStartInfo
        {
            FileName = "docker",
            Arguments = $"compose -p {safeProjectName} down",
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
            if (!string.IsNullOrWhiteSpace(e.Data))
                _logger.LogDebug("[Docker Compose]: {Data}", e.Data);

            _ = _logStreamer.StreamBuildLogsAsync(projectId, e.Data + "\r\n");
        };

        process.ErrorDataReceived += (_, e) =>
        {
            if (!string.IsNullOrWhiteSpace(e.Data))
                _logger.LogWarning("[Docker Compose]: {Data}", e.Data);

            _ = _logStreamer.StreamBuildLogsAsync(projectId, e.Data + "\r\n");
        };

        try
        {
            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(DockerComposeTimeoutMinutes));
            await process.WaitForExitAsync(cts.Token);

            if (process.ExitCode != 0)
            {
                _logger.LogError(
                    "[DockerService] Docker Compose down failed for project '{ProjectName}' with exit code {Code}.",
                    safeProjectName, process.ExitCode);
                return false;
            }

            _logger.LogInformation("[DockerService] Docker Compose down completed successfully for project '{ProjectName}'.",
                safeProjectName);
            return true;
        }
        catch (OperationCanceledException ex)
        {
            _logger.LogError(
                "[DockerService] Docker Compose down timed out after {Timeout} minutes for project '{ProjectName}'." +
                " Exception: {Exception}", ex, DockerComposeTimeoutMinutes, safeProjectName);

            if (!process.HasExited) process.Kill();
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogCritical(ex, "[DockerService] Critical error while launching " +
                                    "Docker Compose down process for '{ProjectName}'.", safeProjectName);
            return false;
        }
    }


    /// <summary>
    ///     Gets a list of all currently running Docker Compose project names.
    /// </summary>
    /// <returns>A list of project names that are currently running.</returns>
    public async Task<List<string>> GetRunningProjectNamesAsync()
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "docker",
            Arguments = "compose ls --format json",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var process = new Process();
        process.StartInfo = startInfo;

        try
        {
            process.Start();
            var output = await process.StandardOutput.ReadToEndAsync();
            await process.WaitForExitAsync(new CancellationTokenSource(TimeSpan.FromSeconds(30)).Token);

            if (process.ExitCode != 0)
            {
                var error = await process.StandardError.ReadToEndAsync();
                _logger.LogWarning("[DockerService] 'docker compose ls' failed. Exit Code: {Code}, Error: {Error}", process.ExitCode, error);
                return [];
            }

            if (string.IsNullOrWhiteSpace(output))
            {
                return [];
            }

            var projects = JsonSerializer.Deserialize<JsonElement>(output);
            var runningProjects = new List<string>();

            if (projects.ValueKind == JsonValueKind.Array)
                foreach (var project in projects.EnumerateArray())
                    if (project.TryGetProperty("Name", out var nameProp) && nameProp.GetString() is { } name)
                        runningProjects.Add(name);

            return runningProjects;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[DockerService] Error executing 'docker compose ls'.");
            return [];
        }
    }


    /// <summary>
    ///     Starts streaming logs for a specified container to the log streamer.
    /// </summary>
    /// <param name="containerName">The name of the container to stream logs from.</param>
    /// <param name="projectId">The ID of the project.</param>
    /// <param name="containerSuffixOrTabId">The tab ID or suffix associated with the container.</param>
    /// <param name="cancellationToken">A token to cancel the streaming process.</param>
    public async Task StreamContainerLogsAsync(string containerName, Guid projectId, string containerSuffixOrTabId,
        CancellationToken cancellationToken)
    {
        try
        {
            _logger.LogInformation("[DockerService] Starting to stream logs for container '{ContainerName}'",
                containerName);
            var logParams = new ContainerLogsParameters
            {
                ShowStdout = true,
                ShowStderr = true,
                Follow = true,
                Tail = "100"
            };

            using var multiplexedStream =
                await _client.Containers.GetContainerLogsAsync(containerName, false, logParams, cancellationToken);

            var buffer = ArrayPool<byte>.Shared.Rent(8192);
            try
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    var readResult = await multiplexedStream.ReadOutputAsync(buffer, 0, buffer.Length, cancellationToken);
                    if (readResult.EOF)
                        break;

                    if (readResult.Count > 0)
                    {
                        var logLine = Encoding.UTF8.GetString(buffer, 0, readResult.Count);
                        await _logStreamer.StreamContainerLogsAsync(projectId, containerSuffixOrTabId, logLine);
                    }
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }
        catch (OperationCanceledException ex)
        {
            _logger.LogInformation(
                "[DockerService] Stopped streaming logs for container '{ContainerName}' (cancelled)," +
                "exception: {Exception}.", containerName, ex.Message);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[DockerService] Error streaming logs for container '{ContainerName}'.",
                containerName);
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
            ignore.Add([
                "bin/",
                "obj/",
                ".git/",
                ".vs/",
                "node_modules/",
                "TestResults/",
                ".DS_Store"
            ]);
        }

        // Create the tar file and write entries while respecting the ignore rules
        await using var fileStream = new FileStream(targetTarFilePath, FileMode.Create, FileAccess.Write);
        await using var tarWriter = new TarWriter(fileStream);

        // Enumerate all files in the source directory and its subdirectories, and write them to the tar file if they are not ignored
        var allFiles = Directory.EnumerateFiles(sourceDirectory, "*.*", SearchOption.AllDirectories);
        foreach (var filePath in allFiles)
        {
            var relativePath = Path.GetRelativePath(sourceDirectory, filePath).Replace('\\', '/');
            if (!ignore.IsIgnored(relativePath))
                await tarWriter.WriteEntryAsync(filePath, relativePath);
        }
    }


    /// <summary>
    ///     Handles the progress of the Docker image build process by logging the output and errors.
    ///     It checks for any error messages in the build output and sets a flag if an error occurs.
    /// </summary>
    /// <param name="msg">The message of the docker build.</param>
    /// <param name="buildErrorOccurred">A reference to an indicator of error occurance.</param>
    private void HandleDockerBuildProgress(JSONMessage msg, ref bool buildErrorOccurred)
    {
        if (!string.IsNullOrEmpty(msg.Stream))
        {
            Console.Write(msg.Stream);
        }

        else if (!string.IsNullOrEmpty(msg.Status))
        {
            if (!string.IsNullOrEmpty(msg.ProgressMessage))
                Console.Write($"\r{msg.Status} {msg.ProgressMessage}");
            else
                Console.WriteLine(msg.Status);
        }

        if (string.IsNullOrEmpty(msg.ErrorMessage)) return;
        _logger.LogError("[DOCKER BUILD ERROR]: {ErrorMessage}", msg.ErrorMessage);
        buildErrorOccurred = true;
    }


    /// <summary>
    ///     Builds the parameters required to create a Docker container based on the provided configuration.
    ///     This includes setting the image, container name, environment variables, and port bindings.
    /// </summary>
    /// <param name="imageTag">The image tag for the container.</param>
    /// <param name="containerName">The name of the container.</param>
    /// <param name="hostPort">The host port for the container.</param>
    /// <param name="containerPort">The container's own port.</param>
    /// <param name="envVarsJson">The environment variables.</param>
    /// <returns></returns>
    private static CreateContainerParameters BuildContainerParameters(
        string imageTag,
        string containerName,
        int hostPort,
        int containerPort,
        string? envVarsJson)
    {
        var envList = new List<string>();
        if (!string.IsNullOrWhiteSpace(envVarsJson))
        {
            var envDict = JsonSerializer.Deserialize<Dictionary<string, string>>(envVarsJson);
            if (envDict != null)
                envList.AddRange(envDict.Select(kv => $"{kv.Key}={kv.Value}"));
        }

        return new CreateContainerParameters
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
    }


    /// <summary>
    ///     Normalizes the project name to create a safe and consistent identifier for Docker Compose.
    /// </summary>
    /// <param name="projectName">The name of the project to be normalized.</param>
    /// <returns>The safe name to be used.</returns>
    private static string NormalizeProjectName(string projectName)
    {
        return projectName.ToLowerInvariant().Replace(" ", "-").Replace(".", "-");
    }


    /// <summary>
    ///     Creates a ProcessStartInfo object configured to run the 'docker compose up -d'
    ///     command with the specified project name and working directory.
    /// </summary>
    /// <param name="workingDir">The path of the working directory.</param>
    /// <param name="safeProjectName">The safe name of the project to be containerized.</param>
    /// <returns></returns>
    private static ProcessStartInfo CreateDockerComposeStartInfo(string workingDir, string safeProjectName)
    {
        return new ProcessStartInfo
        {
            FileName = "docker",
            Arguments = $"compose -p {safeProjectName} up -d --build",
            WorkingDirectory = workingDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
    }
}