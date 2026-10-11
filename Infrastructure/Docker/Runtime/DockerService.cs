using System.Runtime.InteropServices;
using Application.Abstractions.Diagnostics;
using Application.Abstractions.Docker;
using Application.Abstractions.Logging;
using Application.Diagnostics;
using Docker.DotNet;
using Docker.DotNet.Models;
using Infrastructure.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Infrastructure.Docker;

/// <summary>
///     Coordinates Docker daemon operations used by local deployments and runtime log streaming.
/// </summary>
public sealed class DockerService : IDockerService, IDockerDiagnosticSource, IDisposable
{
    /// <summary>
    ///     Helper for packaging Docker build contexts while honoring .dockerignore rules.
    /// </summary>
    private readonly DockerBuildContextArchive _buildContextArchive;

    /// <summary>
    ///     Docker daemon client used for direct Docker Engine operations.
    /// </summary>
    private readonly DockerClient _client;

    /// <summary>Timestamp source for compatibility stream registrations.</summary>
    private readonly TimeProvider _clock;

    /// <summary>Owns normalized, bounded and reconnecting subscriptions.</summary>
    private readonly DockerDiagnosticCollector _collector;

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

    /// <summary>Protects legacy SDK build progress before host logging.</summary>
    private readonly IDiagnosticRedactor _redactor;

    /// <summary>
    ///     Tracks whether the Docker client has already been disposed.
    /// </summary>
    private bool _disposed;

    /// <summary>
    ///     Initializes Docker daemon and CLI helpers using platform-specific Docker connection settings.
    /// </summary>
    public DockerService(ILogger<DockerService> logger, IDeploymentDiagnosticPublisher diagnostics,
        IOptions<DockerOptions> options, IOptions<TelemetryStorageOptions> telemetry,
        ILogStreamer live, IDiagnosticRedactor redactor, IDeploymentRuntimeViewers viewers, TimeProvider clock,
        IServiceScopeFactory scopes)
    {
        _logger = logger;
        _options = options.Value;
        _clock = clock;
        _redactor = redactor;

        var dockerUri = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? new Uri(_options.WindowsDockerUri)
            : new Uri(_options.UnixDockerUri);

        _client = new DockerClientConfiguration(dockerUri).CreateClient();
        _buildContextArchive = new DockerBuildContextArchive(_options, _logger);
        _dockerCli = new DockerCli(_options, diagnostics, _logger, telemetry.Value.RuntimeSampleSeconds,
            live, redactor, viewers, clock);
        _collector = new DockerDiagnosticCollector(_client, diagnostics, redactor, clock, _logger, scopes);
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
    public Task MonitorDaemonAsync(DockerDeploymentTarget target, Action subscribed,
        CancellationToken cancellationToken)
    {
        return _collector.MonitorDaemonAsync(target, subscribed, cancellationToken);
    }

    /// <inheritdoc />
    public Task MonitorContainerAsync(DockerDeploymentTarget target, DockerContainerTarget container,
        CancellationToken cancellationToken)
    {
        return _collector.MonitorContainerAsync(target, container, cancellationToken);
    }

    /// <inheritdoc />
    public async Task MonitorMetricsAsync(DockerDeploymentTarget target, DockerContainerTarget container,
        CancellationToken cancellationToken)
    {
        var id = await _collector.VerifyContainerAsync(target, container, cancellationToken);
        if (id is not null)
            await _dockerCli.StreamContainerMetricsAsync(id, target.ProjectId, target.DeploymentId, container.Channel,
                cancellationToken);
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
            _logger.LogWarning("Docker daemon ping unavailable: {FailureType}.", ex.GetType().Name);
            return false;
        }
    }

    /// <inheritdoc />
    public async Task<bool> BuildImageAsync(string sourcePath, string imageTag,
        CancellationToken cancellationToken = default)
    {
        using var activity = DeploymentTracing.Start(DeploymentOperation.DockerBuild);
        var tempTarFilePath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid()}.tar");

        try
        {
            _logger.LogInformation("Building Docker image.");

            await _buildContextArchive.CreateAsync(sourcePath, tempTarFilePath, cancellationToken);

            await using var fileStream = new FileStream(tempTarFilePath, FileMode.Open, FileAccess.Read,
                FileShare.Read, 81920, FileOptions.Asynchronous | FileOptions.SequentialScan);

            var buildParameters = new ImageBuildParameters { Tags = [imageTag] };
            var buildProgress = new DockerBuildProgress(_logger, _redactor);

            await _client.Images.BuildImageFromDockerfileAsync(
                buildParameters,
                fileStream,
                null,
                null,
                buildProgress,
                cancellationToken);

            if (!buildProgress.HasError)
                _logger.LogInformation("Docker image built successfully.");

            DeploymentTracing.Finish(activity,
                buildProgress.HasError ? DeploymentTraceOutcome.Failed : DeploymentTraceOutcome.Completed);
            return !buildProgress.HasError;
        }
        catch (OperationCanceledException ex)
        {
            DeploymentTracing.Finish(activity,
                cancellationToken.IsCancellationRequested
                    ? DeploymentTraceOutcome.Canceled
                    : DeploymentTraceOutcome.Failed);
            _logger.LogWarning("Docker image build canceled: {FailureType}.", ex.GetType().Name);
            return false;
        }
        catch (Exception ex)
        {
            DeploymentTracing.Finish(activity, DeploymentTraceOutcome.Failed);
            _logger.LogError("Docker image build unavailable: {FailureType}.", ex.GetType().Name);
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

            _logger.LogInformation("Starting Docker container with port {HostPort} mapped to {ContainerPort}.",
                hostPort, actualContainerPort);

            var createParams =
                DockerContainerParameters.Create(imageTag, containerName, hostPort, actualContainerPort, envVarsJson);
            var response = await _client.Containers.CreateContainerAsync(createParams, cancellationToken);
            var containerId = response.ID;

            var started =
                await _client.Containers.StartContainerAsync(containerId, new ContainerStartParameters(),
                    cancellationToken);

            if (started)
            {
                _logger.LogInformation("Docker container started successfully.");
                return containerId;
            }

            _logger.LogWarning("Docker container was created but failed to start.");
            return null;
        }
        catch (OperationCanceledException ex)
        {
            _logger.LogWarning("Docker container start canceled: {FailureType}.", ex.GetType().Name);
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogError("Docker container start unavailable: {FailureType}.", ex.GetType().Name);
            return null;
        }
    }

    /// <inheritdoc />
    public async Task<bool> RunDockerComposeUpAsync(string workingDir, string projectName, Guid projectId,
        Guid deploymentId,
        CancellationToken cancellationToken = default)
    {
        var safeProjectName = DockerNameNormalizer.NormalizeProjectName(projectName);
        _logger.LogInformation("Starting Docker Compose up for project {ProjectId}, deployment {DeploymentId}.",
            projectId, deploymentId);

        return await _dockerCli.RunComposeAsync(workingDir, safeProjectName, projectId, deploymentId, cancellationToken,
            "up", "-d", "--build");
    }

    /// <inheritdoc />
    public async Task<bool> RunDockerComposeDownAsync(string workingDir, string projectName, Guid projectId,
        CancellationToken cancellationToken = default, Guid? deploymentId = null)
    {
        var safeProjectName = DockerNameNormalizer.NormalizeProjectName(projectName);
        _logger.LogInformation("Starting Docker Compose down for project {ProjectId}, deployment {DeploymentId}.",
            projectId, deploymentId);

        return await _dockerCli.RunComposeAsync(workingDir, safeProjectName, projectId, deploymentId, cancellationToken,
            "down");
    }

    /// <inheritdoc />
    public async Task<List<string>> GetRunningProjectNamesAsync(CancellationToken cancellationToken = default)
    {
        return await _dockerCli.GetRunningProjectNamesAsync(cancellationToken);
    }

    /// <inheritdoc />
    public Task StreamContainerLogsAsync(string containerName, Guid projectId, Guid deploymentId,
        string containerSuffixOrTabId, CancellationToken cancellationToken)
    {
        var container =
            new DockerContainerTarget(containerName, containerSuffixOrTabId, containerSuffixOrTabId != "web");
        var target = new DockerDeploymentTarget(projectId, deploymentId, "", [container], _clock.GetUtcNow());
        return _collector.MonitorContainerAsync(target, container, cancellationToken);
    }

    /// <inheritdoc />
    public async Task StreamContainerMetricsAsync(string containerName, Guid projectId, Guid deploymentId,
        string containerSuffixOrTabId,
        CancellationToken cancellationToken)
    {
        await _dockerCli.StreamContainerMetricsAsync(containerName, projectId, deploymentId, containerSuffixOrTabId,
            cancellationToken);
    }

    /// <inheritdoc />
    public async Task<int> GetContainerHostPortAsync(string containerName,
        CancellationToken cancellationToken = default)
    {
        return await _dockerCli.GetContainerHostPortAsync(containerName, cancellationToken);
    }
}