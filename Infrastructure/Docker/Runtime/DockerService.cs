using System.Runtime.InteropServices;
using Application.Abstractions.Diagnostics.Delivery;
using Application.Abstractions.Diagnostics.Events;
using Application.Abstractions.Diagnostics.Redaction;
using Application.Abstractions.Docker;
using Application.Abstractions.Logging;
using Application.Diagnostics;
using Docker.DotNet;
using Docker.DotNet.Models;
using Infrastructure.Diagnostics.Policy;
using Infrastructure.Docker.Build;
using Infrastructure.Docker.Cli;
using Infrastructure.Docker.Compose;
using Infrastructure.Docker.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Infrastructure.Docker.Runtime;

/// <summary>
///     Coordinates Docker daemon operations used by local deployments and runtime log
///     streaming.
/// </summary>
public sealed class DockerService : IDockerService, IDockerDiagnosticSource, IDisposable
{
    /// <summary>Helper for packaging Docker build contexts while honoring .dockerignore rules.</summary>
    private readonly DockerBuildContextArchive _buildContextArchive;

    /// <summary>Docker daemon client used for direct Docker Engine operations.</summary>
    private readonly DockerClient _client;

    /// <summary>Timestamp source for compatibility stream registrations.</summary>
    private readonly TimeProvider _clock;

    /// <summary>Owns normalized, bounded and reconnecting subscriptions.</summary>
    private readonly DockerDiagnosticCollector _collector;

    /// <summary>Helper for Docker CLI operations that are not covered by Docker.DotNet.</summary>
    private readonly DockerCli _dockerCli;

    /// <summary>Logger for Docker service operations.</summary>
    private readonly ILogger<DockerService> _logger;

    /// <summary>Runtime options bound from the Docker configuration section.</summary>
    private readonly DockerOptions _options;

    /// <summary>Protects legacy SDK build progress before host logging.</summary>
    private readonly IDiagnosticRedactor _redactor;

    /// <summary>Tracks whether the Docker client has already been disposed.</summary>
    private bool _disposed;

    /// <summary>
    ///     Initializes Docker daemon and CLI helpers using platform-specific Docker connection
    ///     settings.
    /// </summary>
    /// <param name="logger">
    ///     Receives reviewed operational events without diagnostic payloads or credentials.
    /// </param>
    /// <param name="diagnostics">Accepts normalized observations for redaction and durable delivery.</param>
    /// <param name="options">Provides the configured limits and endpoint settings for this service.</param>
    /// <param name="telemetry">Provides the configured limits and endpoint settings for this service.</param>
    /// <param name="live">Delivers redacted observations to authorized browser groups.</param>
    /// <param name="redactor">Applies shared credential and unsafe-text masking.</param>
    /// <param name="viewers">Provides the deployment runtime viewers boundary used by this operation.</param>
    /// <param name="clock">Time source used for deterministic sampling and deadlines.</param>
    /// <param name="scopes">Creates independent scopes for concurrent background work.</param>
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

    /// <summary>
    ///     Releases owned resources and completes the documented service lifetime.
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
            return;

        _client.Dispose();
        GC.SuppressFinalize(this);
        _disposed = true;
    }

    /// <summary>
    ///     Monitors daemon events; signals the first subscription attempt so deployment can proceed
    ///     safely.
    /// </summary>
    /// <param name="target">Verified project/deployment collector inventory and ownership labels.</param>
    /// <param name="subscribed">Called after the diagnostic source has established its subscription.</param>
    /// <param name="cancellationToken">Cancels the operation when supported by the underlying work.</param>
    /// <returns>A task representing daemon monitoring until cancellation or source termination.</returns>
    public Task MonitorDaemonAsync(DockerDeploymentTarget target, Action subscribed,
        CancellationToken cancellationToken)
    {
        return _collector.MonitorDaemonAsync(target, subscribed, cancellationToken);
    }

    /// <summary>
    ///     Streams a verified project's container with bounded parsing, replay and reconnect
    ///     handling.
    /// </summary>
    /// <param name="target">Verified project/deployment collector inventory and ownership labels.</param>
    /// <param name="container">Verified owned Docker container and terminal identity.</param>
    /// <param name="cancellationToken">Cancels the operation when supported by the underlying work.</param>
    /// <returns>A task representing owned-container log monitoring until cancellation or termination.</returns>
    public Task MonitorContainerAsync(DockerDeploymentTarget target, DockerContainerTarget container,
        CancellationToken cancellationToken)
    {
        return _collector.MonitorContainerAsync(target, container, cancellationToken);
    }

    /// <summary>
    ///     Samples a verified container while preserving the independent live/history metric
    ///     cadence.
    /// </summary>
    /// <param name="target">Verified project/deployment collector inventory and ownership labels.</param>
    /// <param name="container">Verified owned Docker container and terminal identity.</param>
    /// <param name="cancellationToken">Cancels the operation when supported by the underlying work.</param>
    /// <returns>
    ///     A task representing owned-container metric monitoring until cancellation or termination.
    /// </returns>
    public async Task MonitorMetricsAsync(DockerDeploymentTarget target, DockerContainerTarget container,
        CancellationToken cancellationToken)
    {
        var id = await _collector.VerifyContainerAsync(target, container, cancellationToken);
        if (id is not null)
            await _dockerCli.StreamContainerMetricsAsync(id, target.ProjectId, target.DeploymentId, container.Channel,
                cancellationToken);
    }

    /// <summary>
    ///     Checks if the Docker daemon is available and responsive.
    /// </summary>
    /// <param name="cancellationToken">Cancels the operation when supported by the underlying work.</param>
    /// <returns>A task returning whether the Docker daemon is reachable.</returns>
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

    /// <summary>
    ///     Builds a Docker image from a source code directory.
    /// </summary>
    /// <param name="sourcePath">The Docker build-context directory.</param>
    /// <param name="imageTag">The Docker image tag to build or run.</param>
    /// <param name="cancellationToken">Cancels the operation when supported by the underlying work.</param>
    /// <returns>A task returning whether the Docker image build succeeded.</returns>
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

    /// <summary>
    ///     Starts a Docker container from a specified image, mapping the given host port to the
    ///     container port.
    /// </summary>
    /// <param name="imageTag">The Docker image tag to build or run.</param>
    /// <param name="containerName">The exact Docker container name.</param>
    /// <param name="hostPort">The port exposed on the Docker host.</param>
    /// <param name="containerPort">The application port inside the container.</param>
    /// <param name="envVarsJson">The JSON environment-variable map supplied to the container.</param>
    /// <param name="cancellationToken">Cancels the operation when supported by the underlying work.</param>
    /// <returns>A task returning the started container identity, or null when startup fails.</returns>
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

    /// <summary>
    ///     Executes the 'docker compose up' command in the specified working directory.
    /// </summary>
    /// <param name="workingDir">The local directory containing the Compose deployment.</param>
    /// <param name="projectName">The project name used for deployment artifacts or Compose lookup.</param>
    /// <param name="projectId">The project identity used for authorization or correlation.</param>
    /// <param name="deploymentId">The exact deployment identity used for ownership or correlation.</param>
    /// <param name="cancellationToken">Cancels the operation when supported by the underlying work.</param>
    /// <returns>A task returning whether Compose up completed successfully.</returns>
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

    /// <summary>
    ///     Executes the 'docker compose down' command in the specified working directory.
    /// </summary>
    /// <param name="workingDir">The local directory containing the Compose deployment.</param>
    /// <param name="projectName">The project name used for deployment artifacts or Compose lookup.</param>
    /// <param name="projectId">The project identity used for authorization or correlation.</param>
    /// <param name="deploymentId">
    ///     The deployment identity; null is used only where legacy observations permit it.
    /// </param>
    /// <param name="cancellationToken">Cancels the operation when supported by the underlying work.</param>
    /// <returns>A task returning whether Compose down completed successfully.</returns>
    public async Task<bool> RunDockerComposeDownAsync(string workingDir, string projectName, Guid projectId,
        Guid? deploymentId = null, CancellationToken cancellationToken = default)
    {
        var safeProjectName = DockerNameNormalizer.NormalizeProjectName(projectName);
        _logger.LogInformation("Starting Docker Compose down for project {ProjectId}, deployment {DeploymentId}.",
            projectId, deploymentId);

        return await _dockerCli.RunComposeAsync(workingDir, safeProjectName, projectId, deploymentId, cancellationToken,
            "down");
    }

    /// <summary>
    ///     Gets a list of all currently running Docker Compose project names.
    /// </summary>
    /// <param name="cancellationToken">Cancels the operation when supported by the underlying work.</param>
    /// <returns>A task returning the discovered running Compose project names.</returns>
    public async Task<List<string>> GetRunningProjectNamesAsync(CancellationToken cancellationToken = default)
    {
        return await _dockerCli.GetRunningProjectNamesAsync(cancellationToken);
    }

    /// <summary>
    ///     Starts streaming logs for a specified container to the log streamer.
    /// </summary>
    /// <param name="containerName">The exact Docker container name.</param>
    /// <param name="projectId">The project identity used for authorization or correlation.</param>
    /// <param name="deploymentId">The exact deployment identity used for ownership or correlation.</param>
    /// <param name="containerSuffixOrTabId">The stable container terminal suffix used by the browser.</param>
    /// <param name="cancellationToken">Cancels the operation when supported by the underlying work.</param>
    /// <returns>A task representing the container-log subscription until cancellation or termination.</returns>
    public Task StreamContainerLogsAsync(string containerName, Guid projectId, Guid deploymentId,
        string containerSuffixOrTabId, CancellationToken cancellationToken)
    {
        var container =
            new DockerContainerTarget(containerName, containerSuffixOrTabId, containerSuffixOrTabId != "web");
        var target = new DockerDeploymentTarget(projectId, deploymentId, "", [container], _clock.GetUtcNow());
        return _collector.MonitorContainerAsync(target, container, cancellationToken);
    }

    /// <summary>
    ///     Starts streaming metrics for a specified container to the log streamer.
    /// </summary>
    /// <param name="containerName">The exact Docker container name.</param>
    /// <param name="projectId">The project identity used for authorization or correlation.</param>
    /// <param name="deploymentId">The exact deployment identity used for ownership or correlation.</param>
    /// <param name="containerSuffixOrTabId">The stable container terminal suffix used by the browser.</param>
    /// <param name="cancellationToken">Cancels the operation when supported by the underlying work.</param>
    /// <returns>
    ///     A task representing the container-metric subscription until cancellation or termination.
    /// </returns>
    public async Task StreamContainerMetricsAsync(string containerName, Guid projectId, Guid deploymentId,
        string containerSuffixOrTabId,
        CancellationToken cancellationToken)
    {
        await _dockerCli.StreamContainerMetricsAsync(containerName, projectId, deploymentId, containerSuffixOrTabId,
            cancellationToken);
    }

    /// <summary>
    ///     Gets the host port mapped to the specified container.
    /// </summary>
    /// <param name="containerName">The exact Docker container name.</param>
    /// <param name="cancellationToken">Cancels the operation when supported by the underlying work.</param>
    /// <returns>A task returning the discovered host port, or zero when no port is available.</returns>
    public async Task<int> GetContainerHostPortAsync(string containerName,
        CancellationToken cancellationToken = default)
    {
        return await _dockerCli.GetContainerHostPortAsync(containerName, cancellationToken);
    }
}
