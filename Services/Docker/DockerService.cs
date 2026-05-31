using System.Buffers;
using System.Diagnostics;
using System.Formats.Tar;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Docker.DotNet;
using Docker.DotNet.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Services.LogStreaming;

namespace Services.Docker;

/// <summary>
///     The <see cref="DockerService" /> class provides functionality to interact with the Docker daemon.
///     It allows checking the availability of the Docker service and is designed to handle Docker-related
///     operations, such as building and deploying projects. The class also implements the IDisposable interface
///     to ensure proper resource management when working with the Docker client.
/// </summary>
public partial class DockerService : IDockerService, IDisposable
{
    private readonly DockerClient _client;

    private readonly ILogger<DockerService> _logger;
    private readonly ILogStreamer _logStreamer;
    private readonly DockerOptions _options;

    private bool _disposed;


    /// <summary>
    ///     Initializes a new instance of the <see cref="DockerService" /> class. It sets up
    ///     the Docker client configuration based on the operating system. For Windows, it
    ///     uses a named pipe to connect to the Docker daemon, while for Unix-based systems,
    ///     it uses a Unix socket.
    /// </summary>
    public DockerService(ILogger<DockerService> logger, ILogStreamer logStreamer, IOptions<DockerOptions> options)
    {
        _logger = logger;
        _logStreamer = logStreamer;
        _options = options.Value;

        var dockerUri = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? new Uri(_options.WindowsDockerUri)
            : new Uri(_options.UnixDockerUri);

        _client = new DockerClientConfiguration(dockerUri).CreateClient();
    }

    /// <summary>
    ///     Disposes of the resources used by the DockerService instance.
    ///     This method ensures that the Docker client is properly disposed of,
    /// </summary>
    public void Dispose()
    {
        if (_disposed) return;
        _client.Dispose();
        GC.SuppressFinalize(this);
        _disposed = true;
    }


    /// <summary>
    ///     Checks if the Docker daemon is available and responsive by sending a ping request.
    /// </summary>
    /// <param name="cancellationToken">A cancellation token that can be used to cancel the ping operation.</param>
    /// <returns>A boolean indicating if the Docker daemon is available.</returns>
    public async Task<bool> PingAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            await _client.System.PingAsync(cancellationToken);
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
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
    /// <param name="cancellationToken">A cancellation token that can be used to cancel the build operation.</param>
    /// <returns>
    ///     A task representing the asynchronous operation, returning true if the image was successfully built,
    ///     otherwise false.
    /// </returns>
    public async Task<bool> BuildImageAsync(string sourcePath, string imageTag,
        CancellationToken cancellationToken = default)
    {
        var tempTarFilePath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid()}.tar");

        try
        {
            _logger.LogInformation("[DockerService] Building Docker image '{ImageTag}' from: {SourcePath}", imageTag,
                sourcePath);

            await CreateTarContextAsync(sourcePath, tempTarFilePath, cancellationToken);

            await using var fileStream = new FileStream(tempTarFilePath, FileMode.Open, FileAccess.Read,
                FileShare.Read, 81920, FileOptions.Asynchronous | FileOptions.SequentialScan);

            var buildParameters = new ImageBuildParameters { Tags = [imageTag] };
            var buildErrorOccurred = false;

            await _client.Images.BuildImageFromDockerfileAsync(
                buildParameters,
                fileStream,
                null,
                null,
                new Progress<JSONMessage>(msg => HandleDockerBuildProgress(msg, ref buildErrorOccurred)),
                cancellationToken);

            if (!buildErrorOccurred)
                _logger.LogInformation("[DockerService] Docker image '{ImageTag}' built successfully.", imageTag);

            return !buildErrorOccurred;
        }
        catch (OperationCanceledException ex)
        {
            _logger.LogWarning("[DockerService] Build operation cancelled for image '{ImageTag}', " +
                               "Exception: {ExceptionMessage}", imageTag, ex.Message);
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[DockerService] Error building Docker image '{ImageTag}'.", imageTag);
            return false;
        }
        finally
        {
            DeleteTempFile(tempTarFilePath);
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
    /// <param name="cancellationToken">A cancellation token that can be used to cancel the start operation.</param>
    /// <returns>The ID of the started container if the operation is successful, or null if it fails.</returns>
    public async Task<string?> StartContainerAsync(string imageTag, string containerName, int hostPort,
        int containerPort = 8080, string? envVarsJson = null, CancellationToken cancellationToken = default)
    {
        try
        {
            var actualContainerPort = containerPort == 8080 ? _options.DefaultContainerPort : containerPort;

            _logger.LogInformation(
                "[DockerService] Starting container '{ContainerName}' (Image: {ImageTag}, Port: {HostPort}->{ContainerPort})",
                containerName, imageTag, hostPort, actualContainerPort);

            var createParams =
                BuildContainerParameters(imageTag, containerName, hostPort, actualContainerPort, envVarsJson);
            var response = await _client.Containers.CreateContainerAsync(createParams, cancellationToken);
            var containerId = response.ID;

            var started =
                await _client.Containers.StartContainerAsync(containerId, new ContainerStartParameters(),
                    cancellationToken);

            if (started)
            {
                _logger.LogInformation(
                    "[DockerService] Container '{ContainerName}' ({ContainerId}) started successfully.", containerName,
                    containerId[..8]);
                return containerId;
            }

            _logger.LogWarning("[DockerService] Container '{ContainerName}' was created but failed to start.",
                containerName);
            return null;
        }
        catch (OperationCanceledException ex)
        {
            _logger.LogWarning("[DockerService] Start operation cancelled for container '{ContainerName}'." +
                               "Exception: {ExceptionMessage}", containerName, ex.Message);
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
    /// <param name="cancellationToken">A cancellation token that can be used to cancel the operation.</param>
    /// <returns>Returns an indicator if the docker compose up command was successfully run.</returns>
    public async Task<bool> RunDockerComposeUpAsync(string workingDir, string projectName, Guid projectId,
        CancellationToken cancellationToken = default)
    {
        var safeProjectName = NormalizeProjectName(projectName);
        _logger.LogInformation(
            "[DockerService] Starting 'docker compose up -d' for project '{ProjectName}' in {Directory}",
            safeProjectName, workingDir);

        var startInfo = CreateDockerComposeStartInfo(workingDir, safeProjectName);
        return await ExecuteProcessStreamingLogsAsync(startInfo, projectId, cancellationToken);
    }


    /// <summary>
    ///     Executes the 'docker compose down' command in a specified working directory with a given project name.
    /// </summary>
    /// <param name="workingDir">The working directory where the docker command should be run.</param>
    /// <param name="projectName">The name of the project whose containers should be stopped.</param>
    /// <param name="projectId">The ID of the project.</param>
    /// <param name="cancellationToken">A cancellation token that can be used to cancel the operation.</param>
    /// <returns>Returns an indicator if the docker compose down command was successfully run.</returns>
    public async Task<bool> RunDockerComposeDownAsync(string workingDir, string projectName, Guid projectId,
        CancellationToken cancellationToken = default)
    {
        var safeProjectName = NormalizeProjectName(projectName);
        _logger.LogInformation(
            "[DockerService] Starting 'docker compose down' for project '{ProjectName}' in {Directory}",
            safeProjectName, workingDir);

        var startInfo = CreateDockerComposeStartInfo(workingDir, safeProjectName, "down");

        return await ExecuteProcessStreamingLogsAsync(startInfo, projectId, cancellationToken);
    }


    /// <summary>
    ///     Gets a list of all currently running Docker Compose project names.
    /// </summary>
    /// <param name="cancellationToken">A cancellation token that can be used to cancel the operation.</param>
    /// <returns>A list of project names that are currently running.</returns>
    public async Task<List<string>> GetRunningProjectNamesAsync(CancellationToken cancellationToken = default)
    {
        var startInfo = CreateDockerProcessStartInfo();
        startInfo.ArgumentList.Add("compose");
        startInfo.ArgumentList.Add("ls");
        startInfo.ArgumentList.Add("--format");
        startInfo.ArgumentList.Add("json");

        using var process = new Process();
        process.StartInfo = startInfo;

        try
        {
            process.Start();

            // Set a timeout for reading the output to prevent hanging if the command fails
            using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);

            var outputTask = process.StandardOutput.ReadToEndAsync(linkedCts.Token);
            var errorTask = process.StandardError.ReadToEndAsync(linkedCts.Token);
            await process.WaitForExitAsync(linkedCts.Token);

            var output = await outputTask;
            var error = await errorTask;

            if (process.ExitCode != 0)
            {
                _logger.LogWarning("[DockerService] 'docker compose ls' failed. Exit Code: {Code}, Error: {Error}",
                    process.ExitCode, error);
                return [];
            }

            if (string.IsNullOrWhiteSpace(output)) return [];

            var projects = JsonSerializer.Deserialize<JsonElement>(output);
            var runningProjects = new List<string>();

            if (projects.ValueKind == JsonValueKind.Array)
                foreach (var project in projects.EnumerateArray())
                    if (project.TryGetProperty("Name", out var nameProp) && nameProp.GetString() is { } name)
                        runningProjects.Add(name);

            return runningProjects;
        }
        catch (OperationCanceledException ex)
        {
            _logger.LogWarning("[DockerService] 'docker compose ls' operation was cancelled or timed out. " +
                               "Exception: {Exception}", ex.Message);

            if (!process.HasExited)
                process.Kill(true);

            return [];
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
                ShowStdout = true, ShowStderr = true, Follow = true, Tail = "100"
            };

            using var multiplexedStream =
                await _client.Containers.GetContainerLogsAsync(containerName, false, logParams, cancellationToken);

            var buffer = ArrayPool<byte>.Shared.Rent(8192);

            try
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    var readResult =
                        await multiplexedStream.ReadOutputAsync(buffer, 0, buffer.Length, cancellationToken);
                    if (readResult.EOF) break;

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
    ///     Starts streaming metrics for a specified container to the log streamer.
    /// </summary>
    /// <param name="containerName">The name of the container to stream metrics from.</param>
    /// <param name="projectId">The ID of the project.</param>
    /// <param name="containerSuffixOrTabId">The tab ID or suffix associated with the container.</param>
    /// <param name="cancellationToken">A token to cancel the streaming process.</param>
    public async Task StreamContainerMetricsAsync(string containerName, Guid projectId, string containerSuffixOrTabId,
        CancellationToken cancellationToken)
    {
        try
        {
            _logger.LogInformation("[DockerService] Starting to stream metrics for container '{ContainerName}'",
                containerName);

            var startInfo = CreateDockerProcessStartInfo(false);
            startInfo.ArgumentList.Add("stats");
            startInfo.ArgumentList.Add(containerName);
            startInfo.ArgumentList.Add("--format");
            startInfo.ArgumentList.Add("{{.CPUPerc}}|{{.MemUsage}}");

            using var process = Process.Start(startInfo);
            if (process == null) return;

            // Register a cancellation callback to kill the process if the token is cancelled
            await using var registration = cancellationToken.Register(() =>
            {
                try
                {
                    if (!process.HasExited)
                        process.Kill(true);
                }
                catch
                {
                    /* ignore */
                }
            });

            using var reader = process.StandardOutput;
            while (!cancellationToken.IsCancellationRequested)
            {
                // Read each line of output, which contains the CPU and memory usage separated by '|'
                var line = await reader.ReadLineAsync(cancellationToken);
                if (line == null)
                    break;

                if (string.IsNullOrWhiteSpace(line))
                    continue;

                line = MetricsRegex().Replace(line, "");
                var parts = line.Split('|');

                if (parts.Length >= 2)
                    await _logStreamer.StreamContainerMetricsAsync(
                        projectId,
                        containerSuffixOrTabId,
                        parts[0].Trim(),
                        parts[1].Trim()
                    );
            }
        }
        catch (OperationCanceledException ex)
        {
            _logger.LogInformation(
                "[DockerService] Stopped streaming metrics for container '{ContainerName}' (cancelled)." +
                "Exception: {Exception}.", containerName, ex.Message);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[DockerService] Error streaming metrics for container '{ContainerName}'.",
                containerName);
        }
    }


    /// <summary>
    ///     Gets the host port mapped to the specified container.
    /// </summary>
    public async Task<int> GetContainerHostPortAsync(string containerName,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var startInfo = CreateDockerProcessStartInfo(false);
            startInfo.ArgumentList.Add("port");
            startInfo.ArgumentList.Add(containerName);

            using var process = new Process();
            process.StartInfo = startInfo;
            process.Start();

            // Set a timeout for reading the output to prevent hanging if the command fails
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(TimeSpan.FromSeconds(10));

            var outputTask = process.StandardOutput.ReadToEndAsync(cts.Token);
            await process.WaitForExitAsync(cts.Token);
            var output = await outputTask;

            if (process.ExitCode == 0 && !string.IsNullOrWhiteSpace(output))
            {
                var match = HostPortRegex().Match(output);
                if (match.Success && int.TryParse(match.Groups[1].Value, out var port)) return port;
            }
        }
        catch (OperationCanceledException ex)
        {
            _logger.LogWarning("[DockerService] Getting host port for container '{ContainerName}' was cancelled." +
                               "Exception: {Exception}.", containerName, ex.Message);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[DockerService] Error getting host port for container '{ContainerName}'.",
                containerName);
        }

        return 0;
    }


    /// <summary>
    ///     Executes a process with the specified start information and streams its output and error logs to the log streamer.
    /// </summary>
    /// <param name="startInfo"> Process start information for the command to be executed. </param>
    /// <param name="projectId"> Project identifier for logging purposes. </param>
    /// <param name="cancellationToken"> Token for cancellation of the operation. </param>
    /// <returns></returns>
    private async Task<bool> ExecuteProcessStreamingLogsAsync(ProcessStartInfo startInfo, Guid projectId,
        CancellationToken cancellationToken)
    {
        using var process = new Process();
        process.StartInfo = startInfo;

        process.OutputDataReceived += (_, e) =>
        {
            if (!string.IsNullOrWhiteSpace(e.Data))
                _logStreamer.StreamBuildLogsAsync(projectId, e.Data + "\r\n");
        };

        process.ErrorDataReceived += (_, e) =>
        {
            if (!string.IsNullOrWhiteSpace(e.Data))
                _logStreamer.StreamBuildLogsAsync(projectId, e.Data + "\r\n");
        };

        try
        {
            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            using var timeoutCts = new CancellationTokenSource(TimeSpan.FromMinutes(_options.ComposeTimeoutMinutes));
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);

            await process.WaitForExitAsync(linkedCts.Token);

            if (process.ExitCode != 0)
            {
                _logger.LogError("[DockerService] Process failed with exit code {Code}. Command: {Cmd}",
                    process.ExitCode, CreateProcessCommandDescription(startInfo));
                return false;
            }

            return true;
        }
        catch (OperationCanceledException ex)
        {
            _logger.LogError("[DockerService] Process timed out or was cancelled. Command: {Cmd}," +
                             " Exception: {Ex}", CreateProcessCommandDescription(startInfo), ex.Message);
            if (!process.HasExited)
                process.Kill(true);
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogCritical(ex, "[DockerService] Critical error launching process. Command: {Cmd}",
                CreateProcessCommandDescription(startInfo));
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
    /// <param name="cancellationToken">A cancellation token that can be used to cancel the operation.</param>
    private async Task CreateTarContextAsync(string sourceDirectory, string targetTarFilePath,
        CancellationToken cancellationToken)
    {
        if (!Directory.Exists(sourceDirectory))
            throw new DirectoryNotFoundException($"Source directory '{sourceDirectory}' does not exist.");

        // Initilize the ignore rules
        var ignore = new Ignore.Ignore();
        var dockerIgnorePath = Path.Combine(sourceDirectory, ".dockerignore");

        if (File.Exists(dockerIgnorePath))
        {
            var lines = await File.ReadAllLinesAsync(dockerIgnorePath, cancellationToken);
            ignore.Add(lines);
        }
        else
        {
            ignore.Add(_options.DefaultDockerIgnore);
        }

        // Create the tar file and write entries while respecting the ignore rules
        await using var fileStream = new FileStream(targetTarFilePath, FileMode.Create, FileAccess.Write,
            FileShare.None, 81920, FileOptions.Asynchronous | FileOptions.SequentialScan);

        await using var tarWriter = new TarWriter(fileStream);

        // Enumerate all files in the source directory and its subdirectories, and write them to the tar file if they are not ignored
        var allFiles = Directory.EnumerateFiles(sourceDirectory, "*", SearchOption.AllDirectories);
        foreach (var filePath in allFiles)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var relativePath = Path.GetRelativePath(sourceDirectory, filePath).Replace('\\', '/');
            if (!ignore.IsIgnored(relativePath))
                await tarWriter.WriteEntryAsync(filePath, relativePath, cancellationToken);
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
            _logger.LogDebug("[DockerService] {Message}", msg.Stream.TrimEnd());
        }

        else if (!string.IsNullOrEmpty(msg.Status))
        {
            if (!string.IsNullOrEmpty(msg.ProgressMessage))
                _logger.LogDebug("[DockerService] {Status} {Progress}", msg.Status, msg.ProgressMessage);
            else
                _logger.LogDebug("[DockerService] {Status}", msg.Status);
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
    /// <returns>The container creation parameters.</returns>
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
        var normalized = ProjectNameRegex().Replace(projectName.Trim().ToLowerInvariant(), "-");
        normalized = string.Join('-', normalized.Split('-',
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

        return string.IsNullOrWhiteSpace(normalized) ? "automate-project" : normalized;
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
        return CreateDockerComposeStartInfo(workingDir, safeProjectName, "up", "-d", "--build");
    }


    /// <summary>
    ///     Creates a ProcessStartInfo object configured to run a Docker Compose command.
    /// </summary>
    private static ProcessStartInfo CreateDockerComposeStartInfo(string workingDir, string safeProjectName,
        params string[] composeArguments)
    {
        var startInfo = CreateDockerProcessStartInfo();
        startInfo.WorkingDirectory = workingDir;
        startInfo.ArgumentList.Add("compose");
        startInfo.ArgumentList.Add("-p");
        startInfo.ArgumentList.Add(safeProjectName);

        foreach (var argument in composeArguments)
            startInfo.ArgumentList.Add(argument);

        return startInfo;
    }


    /// <summary>
    ///     Creates a ProcessStartInfo object configured to run a Docker command with the specified arguments.
    /// </summary>
    /// <param name="redirectStandardError">Indicates whether to redirect standard error.</param>
    /// <returns>The configured ProcessStartInfo object.</returns>
    private static ProcessStartInfo CreateDockerProcessStartInfo(bool redirectStandardError = true)
    {
        return new ProcessStartInfo
        {
            FileName = "docker",
            RedirectStandardOutput = true,
            RedirectStandardError = redirectStandardError,
            UseShellExecute = false,
            CreateNoWindow = true
        };
    }


    /// <summary>
    ///     Creates a human-readable description of the process command being executed, based on the provided ProcessStartInfo.
    /// </summary>
    /// <param name="startInfo">The ProcessStartInfo object.</param>
    /// <returns>The human-readable description.</returns>
    private static string CreateProcessCommandDescription(ProcessStartInfo startInfo)
    {
        var arguments = startInfo.ArgumentList.Count > 0
            ? string.Join(' ', startInfo.ArgumentList)
            : startInfo.Arguments;

        return $"{startInfo.FileName} {arguments}".Trim();
    }


    /// <summary>
    ///     Deletes the specified temporary file, handling any exceptions that may occur during the deletion process.
    /// </summary>
    /// <param name="path">The path of the temporary file to delete.</param>
    private void DeleteTempFile(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch (IOException ex)
        {
            _logger.LogWarning(ex, "[DockerService] Failed to delete temporary Docker build context '{Path}'.", path);
        }
        catch (UnauthorizedAccessException ex)
        {
            _logger.LogWarning(ex, "[DockerService] Failed to delete temporary Docker build context '{Path}'.", path);
        }
    }


    /// A regular expression to remove ANSI escape codes from the docker stats output.
    [GeneratedRegex(@"\x1B(?:[@-Z\\-_]|\[[0-?]*[ -/]*[@-~])")]
    private static partial Regex MetricsRegex();


    /// A regular expression to extract the host port from the output of the 'docker port' command.
    [GeneratedRegex(@":(\d+)")]
    private static partial Regex HostPortRegex();


    /// A regular expression to normalize Docker Compose project names.
    [GeneratedRegex(@"[^a-z0-9_-]+")]
    private static partial Regex ProjectNameRegex();
}