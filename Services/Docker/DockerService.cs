using System.Buffers;
using System.Runtime.InteropServices;
using System.Text;
using Docker.DotNet;
using Docker.DotNet.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Services.LogStreaming;

namespace Services.Docker;

/// <summary>
///     Coordinates Docker daemon operations used by local deployments and runtime log streaming.
/// </summary>
public sealed class DockerService : IDockerService, IDisposable
{
    /// <summary>
    ///     Helper for packaging Docker build contexts while honoring .dockerignore rules.
    /// </summary>
    private readonly DockerBuildContextArchive _buildContextArchive;

    /// <summary>
    ///     Docker daemon client used for direct Docker Engine operations.
    /// </summary>
    private readonly DockerClient _client;

    /// <summary>
    ///     Helper for Docker CLI operations that are not covered by Docker.DotNet.
    /// </summary>
    private readonly DockerCli _dockerCli;

    /// <summary>
    ///     Logger for Docker service operations.
    /// </summary>
    private readonly ILogger<DockerService> _logger;

    /// <summary>
    ///     Runtime options bound from the Docker configuration section.
    /// </summary>
    private readonly DockerOptions _options;

    /// <summary>
    ///     Tracks whether the Docker client has already been disposed.
    /// </summary>
    private bool _disposed;

    /// <summary>
    ///     Initializes Docker daemon and CLI helpers using platform-specific Docker connection settings.
    /// </summary>
    public DockerService(ILogger<DockerService> logger, ILogStreamer logStreamer, IOptions<DockerOptions> options)
    {
        _logger = logger;
        _options = options.Value;

        var dockerUri = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? new Uri(_options.WindowsDockerUri)
            : new Uri(_options.UnixDockerUri);

        _client = new DockerClientConfiguration(dockerUri).CreateClient();
        _buildContextArchive = new DockerBuildContextArchive(_options, _logger);
        _dockerCli = new DockerCli(_options, logStreamer, _logger);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
            return;

        _client.Dispose();
        GC.SuppressFinalize(this);
        _disposed = true;
    }

    /// <inheritdoc />
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

    /// <inheritdoc />
    public async Task<bool> BuildImageAsync(string sourcePath, string imageTag,
        CancellationToken cancellationToken = default)
    {
        var tempTarFilePath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid()}.tar");

        try
        {
            _logger.LogInformation("[DockerService] Building Docker image '{ImageTag}' from: {SourcePath}", imageTag,
                sourcePath);

            await _buildContextArchive.CreateAsync(sourcePath, tempTarFilePath, cancellationToken);

            await using var fileStream = new FileStream(tempTarFilePath, FileMode.Open, FileAccess.Read,
                FileShare.Read, 81920, FileOptions.Asynchronous | FileOptions.SequentialScan);

            var buildParameters = new ImageBuildParameters { Tags = [imageTag] };
            var buildProgress = new DockerBuildProgress(_logger);

            await _client.Images.BuildImageFromDockerfileAsync(
                buildParameters,
                fileStream,
                null,
                null,
                new Progress<JSONMessage>(buildProgress.Handle),
                cancellationToken);

            if (!buildProgress.HasError)
                _logger.LogInformation("[DockerService] Docker image '{ImageTag}' built successfully.", imageTag);

            return !buildProgress.HasError;
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
            _buildContextArchive.DeleteTempFile(tempTarFilePath);
        }
    }

    /// <inheritdoc />
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
                DockerContainerParameters.Create(imageTag, containerName, hostPort, actualContainerPort, envVarsJson);
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

    /// <inheritdoc />
    public async Task<bool> RunDockerComposeUpAsync(string workingDir, string projectName, Guid projectId,
        CancellationToken cancellationToken = default)
    {
        var safeProjectName = DockerNameNormalizer.NormalizeProjectName(projectName);
        _logger.LogInformation(
            "[DockerService] Starting 'docker compose up -d' for project '{ProjectName}' in {Directory}",
            safeProjectName, workingDir);

        return await _dockerCli.RunComposeAsync(workingDir, safeProjectName, projectId, cancellationToken,
            "up", "-d", "--build");
    }

    /// <inheritdoc />
    public async Task<bool> RunDockerComposeDownAsync(string workingDir, string projectName, Guid projectId,
        CancellationToken cancellationToken = default)
    {
        var safeProjectName = DockerNameNormalizer.NormalizeProjectName(projectName);
        _logger.LogInformation(
            "[DockerService] Starting 'docker compose down' for project '{ProjectName}' in {Directory}",
            safeProjectName, workingDir);

        return await _dockerCli.RunComposeAsync(workingDir, safeProjectName, projectId, cancellationToken, "down");
    }

    /// <inheritdoc />
    public async Task<List<string>> GetRunningProjectNamesAsync(CancellationToken cancellationToken = default)
    {
        return await _dockerCli.GetRunningProjectNamesAsync(cancellationToken);
    }

    /// <inheritdoc />
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
                    var readResult =
                        await multiplexedStream.ReadOutputAsync(buffer, 0, buffer.Length, cancellationToken);
                    if (readResult.EOF)
                        break;

                    if (readResult.Count > 0)
                    {
                        var logLine = Encoding.UTF8.GetString(buffer, 0, readResult.Count);
                        await _dockerCli.StreamContainerLogAsync(projectId, containerSuffixOrTabId, logLine);
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

    /// <inheritdoc />
    public async Task StreamContainerMetricsAsync(string containerName, Guid projectId, string containerSuffixOrTabId,
        CancellationToken cancellationToken)
    {
        await _dockerCli.StreamContainerMetricsAsync(containerName, projectId, containerSuffixOrTabId,
            cancellationToken);
    }

    /// <inheritdoc />
    public async Task<int> GetContainerHostPortAsync(string containerName,
        CancellationToken cancellationToken = default)
    {
        return await _dockerCli.GetContainerHostPortAsync(containerName, cancellationToken);
    }
}