using System.Diagnostics;
using System.Globalization;
using Application.Abstractions.Diagnostics;
using Application.Abstractions.Logging;
using Application.Diagnostics;
using Microsoft.Extensions.Logging;

namespace Infrastructure.Docker;

/// <summary>
///     Executes Docker CLI commands needed for Compose, metrics, port discovery, and project listing.
/// </summary>
internal sealed class DockerCli(
    DockerOptions options,
    IDeploymentDiagnosticPublisher diagnostics,
    ILogger logger,
    int sampleSeconds,
    ILogStreamer live,
    IDiagnosticRedactor redactor,
    IDeploymentRuntimeViewers viewers,
    TimeProvider clock)
{
    /// <summary>
    ///     Timeout used while listing running Docker Compose projects.
    /// </summary>
    private static readonly TimeSpan ComposeListTimeout = TimeSpan.FromSeconds(30);

    /// <summary>
    ///     Timeout used while resolving a container's mapped host port.
    /// </summary>
    private static readonly TimeSpan PortLookupTimeout = TimeSpan.FromSeconds(10);

    /// <summary>
    ///     Executes a Docker Compose command and streams stdout/stderr to the build log channel.
    /// </summary>
    public async Task<bool> RunComposeAsync(string workingDir, string safeProjectName, Guid projectId,
        Guid? deploymentId,
        CancellationToken cancellationToken, params string[] composeArguments)
    {
        var startInfo = DockerProcessStartInfoFactory.CreateCompose(workingDir, safeProjectName, composeArguments);
        if (deploymentId is { } id) startInfo.Environment["AUTOMATE_DEPLOYMENT_ID"] = id.ToString("N");
        return await ExecuteProcessStreamingLogsAsync(startInfo, projectId, deploymentId, cancellationToken);
    }

    /// <summary>
    ///     Returns the names of currently running Docker Compose projects.
    /// </summary>
    public async Task<List<string>> GetRunningProjectNamesAsync(CancellationToken cancellationToken)
    {
        var startInfo = DockerProcessStartInfoFactory.CreateDocker();
        startInfo.ArgumentList.Add("compose");
        startInfo.ArgumentList.Add("ls");
        startInfo.ArgumentList.Add("--format");
        startInfo.ArgumentList.Add("json");

        using var process = new Process();
        process.StartInfo = startInfo;

        try
        {
            process.Start();

            using var timeoutCts = new CancellationTokenSource(ComposeListTimeout);
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);

            var outputTask = process.StandardOutput.ReadToEndAsync(linkedCts.Token);
            var errorTask = process.StandardError.ReadToEndAsync(linkedCts.Token);
            await process.WaitForExitAsync(linkedCts.Token);

            var output = await outputTask;
            var error = await errorTask;

            if (process.ExitCode != 0)
            {
                logger.LogWarning("[DockerService] 'docker compose ls' failed. Exit Code: {Code}, Error: {Error}",
                    process.ExitCode, "Docker command rejected; review daemon permissions.");
                return [];
            }

            return DockerComposeProjectParser.ParseRunningProjectNames(output);
        }
        catch (OperationCanceledException ex)
        {
            logger.LogWarning("[DockerService] 'docker compose ls' operation was cancelled or timed out. " +
                              "Exception: {Exception}", ex.GetType().Name);

            KillProcessTree(process);
            return [];
        }
        catch (Exception ex)
        {
            logger.LogError("Docker Compose listing unavailable: {FailureType}.", ex.GetType().Name);
            return [];
        }
    }

    /// <summary>
    ///     Streams Docker CLI stats output for one container through the metrics channel.
    /// </summary>
    public async Task StreamContainerMetricsAsync(string containerName, Guid projectId, Guid deploymentId,
        string containerSuffixOrTabId,
        CancellationToken cancellationToken)
    {
        using var activity = DeploymentTracing.Start(DeploymentOperation.DockerMetrics, projectId, deploymentId);
        try
        {
            logger.LogInformation("Starting Docker metrics for project {ProjectId}, deployment {DeploymentId}.",
                projectId, deploymentId);

            var startInfo = DockerProcessStartInfoFactory.CreateDocker(false);
            startInfo.ArgumentList.Add("stats");
            startInfo.ArgumentList.Add(containerName);
            startInfo.ArgumentList.Add("--format");
            startInfo.ArgumentList.Add("{{.CPUPerc}}|{{.MemUsage}}");

            using var process = Process.Start(startInfo);
            if (process == null)
            {
                DeploymentTracing.Finish(activity, DeploymentTraceOutcome.Failed);
                return;
            }

            await using var registration = cancellationToken.Register(() => KillProcessTree(process));

            using var reader = process.StandardOutput;
            var delivery = new DockerMetricDelivery(diagnostics, live, redactor, viewers, clock, sampleSeconds, logger);
            while (!cancellationToken.IsCancellationRequested)
            {
                var line = await reader.ReadLineAsync(cancellationToken);
                if (line == null)
                    break;

                if (DockerMetricsLine.TryParse(line, out var metrics))
                    await delivery.ObserveAsync(projectId, deploymentId, containerSuffixOrTabId, metrics,
                        cancellationToken);
            }

            DeploymentTracing.Finish(activity,
                cancellationToken.IsCancellationRequested
                    ? DeploymentTraceOutcome.Canceled
                    : DeploymentTraceOutcome.Completed);
        }
        catch (OperationCanceledException)
        {
            DeploymentTracing.Finish(activity,
                cancellationToken.IsCancellationRequested
                    ? DeploymentTraceOutcome.Canceled
                    : DeploymentTraceOutcome.Failed);
            if (cancellationToken.IsCancellationRequested)
                logger.LogDebug("Docker metric stream stopped after cancellation.");
            else
                logger.LogWarning("Docker metric stream ended after unexpected provider cancellation.");
        }
        catch (Exception ex)
        {
            DeploymentTracing.Finish(activity, DeploymentTraceOutcome.Failed);
            logger.LogError("Docker metric subscription unavailable: {FailureType}.", ex.GetType().Name);
        }
    }

    /// <summary>
    ///     Resolves the host port mapped to the supplied container by parsing Docker CLI output.
    /// </summary>
    public async Task<int> GetContainerHostPortAsync(string containerName, CancellationToken cancellationToken)
    {
        try
        {
            var startInfo = DockerProcessStartInfoFactory.CreateDocker(false);
            startInfo.ArgumentList.Add("port");
            startInfo.ArgumentList.Add(containerName);

            using var process = new Process();
            process.StartInfo = startInfo;
            process.Start();

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(PortLookupTimeout);

            var outputTask = process.StandardOutput.ReadToEndAsync(cts.Token);
            await process.WaitForExitAsync(cts.Token);
            var output = await outputTask;

            return process.ExitCode == 0 ? DockerPortParser.ParseHostPort(output) : 0;
        }
        catch (OperationCanceledException ex)
        {
            logger.LogWarning("Docker host port lookup canceled: {FailureType}.", ex.GetType().Name);
        }
        catch (Exception ex)
        {
            logger.LogError("Docker host port lookup unavailable: {FailureType}.", ex.GetType().Name);
        }

        return 0;
    }

    /// <summary>
    ///     Executes a process and forwards all emitted output to the deployment build log.
    /// </summary>
    private async Task<bool> ExecuteProcessStreamingLogsAsync(ProcessStartInfo startInfo, Guid projectId,
        Guid? deploymentId, CancellationToken cancellationToken)
    {
        using var activity = DeploymentTracing.Start(DeploymentOperation.DockerCompose, projectId, deploymentId);
        using var process = new Process { StartInfo = startInfo };
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromMinutes(options.ComposeTimeoutMinutes));
        var readers = new List<Task>();
        var composeProject = startInfo.ArgumentList[2];
        long sequence = 0;
        var publishing = new object();
        try
        {
            process.Start();
            using var registration = deadline.Token.Register(() => KillProcessTree(process));
            readers.Add(ReadOutputAsync(process.StandardOutput.BaseStream, DeploymentDiagnosticStream.StandardOutput));
            readers.Add(ReadOutputAsync(process.StandardError.BaseStream, DeploymentDiagnosticStream.StandardError));
            await process.WaitForExitAsync(deadline.Token);
            logger.LogInformation("Docker Compose process exited for project {ProjectId} with code {Code}.",
                projectId, process.ExitCode);
            await Task.WhenAll(readers);
            DeploymentTracing.Finish(activity,
                process.ExitCode == 0 ? DeploymentTraceOutcome.Completed : DeploymentTraceOutcome.Failed);
            await OutcomeAsync(process.ExitCode == 0 ? "completed" : "failed", process.ExitCode);
            if (process.ExitCode != 0)
                AutoMateTelemetry.CollectorErrors.Add(1,
                    new KeyValuePair<string, object?>("deployment.source", "DockerCompose"));
            return process.ExitCode == 0;
        }
        catch (OperationCanceledException)
        {
            KillProcessTree(process);
            DeploymentTracing.Finish(activity,
                cancellationToken.IsCancellationRequested
                    ? DeploymentTraceOutcome.Canceled
                    : DeploymentTraceOutcome.TimedOut);
            await OutcomeAsync(cancellationToken.IsCancellationRequested ? "cancelled" : "timed-out", null);
            if (!cancellationToken.IsCancellationRequested)
                AutoMateTelemetry.CollectorErrors.Add(1,
                    new KeyValuePair<string, object?>("deployment.source", "DockerCompose"));
            return false;
        }
        catch (Exception exception)
        {
            KillProcessTree(process);
            AutoMateTelemetry.CollectorErrors.Add(1,
                new KeyValuePair<string, object?>("deployment.source", "DockerCompose"));
            DeploymentTracing.Finish(activity, DeploymentTraceOutcome.Failed);
            logger.LogWarning("Docker Compose command unavailable: {FailureType}.", exception.GetType().Name);
            await OutcomeAsync("unavailable", null);
            return false;
        }
        finally
        {
            // Killing the process releases redirected pipes; every reader is observed before process disposal.
            KillProcessTree(process);
            foreach (var reader in readers)
                try
                {
                    await reader;
                }
                catch (Exception exception)
                {
                    logger.LogDebug("Compose output reader ended: {FailureType}.", exception.GetType().Name);
                }
        }

        /// <summary>Decodes and awaits one redirected stream without asynchronous event handlers.</summary>
        async Task ReadOutputAsync(Stream stream, DeploymentDiagnosticStream output)
        {
            var decoder = new DockerLogDecoder(false, clock);
            var buffer = new byte[8192];
            int count;
            while ((count = await stream.ReadAsync(buffer, deadline.Token)) != 0)
                foreach (var line in decoder.Feed(buffer.AsSpan(0, count)))
                    await PublishLineAsync(line, output);
            foreach (var line in decoder.Feed([], true)) await PublishLineAsync(line, output);
        }

        /// <summary>Serializes event admission and observes asynchronous publisher completion.</summary>
        async Task PublishLineAsync(DockerLogLine line, DeploymentDiagnosticStream output)
        {
            try
            {
                ValueTask pending;
                lock (publishing)
                {
                    var e = DockerDiagnosticNormalizer.Compose(projectId, deploymentId, composeProject, line.Text,
                        output, ++sequence);
                    if (line.Omitted)
                        e = e with
                        {
                            Kind = DeploymentDiagnosticKind.Annotation,
                            Severity = DeploymentDiagnosticSeverity.Warning
                        };
                    pending = diagnostics.PublishAsync(e, cancellationToken);
                }

                await pending;
            }
            catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
            {
                logger.LogWarning("Compose output diagnostic unavailable: {FailureType}.", exception.GetType().Name);
            }
        }

        /// <summary>Reports a finite command outcome without persisting process error bodies.</summary>
        async Task OutcomeAsync(string state, int? exitCode)
        {
            logger.LogInformation("Docker Compose command outcome for project {ProjectId}: {ComposeOutcome}.",
                projectId, state);
            try
            {
                var attributes = new Dictionary<string, string> { ["phase"] = "compose", ["state"] = state };
                if (exitCode.HasValue) attributes["exit_code"] = exitCode.Value.ToString(CultureInfo.InvariantCulture);
                await diagnostics.PublishAsync(new DeploymentDiagnosticEvent(projectId, deploymentId,
                    DeploymentDiagnosticSource.DockerCompose,
                    DeploymentDiagnosticKind.Lifecycle,
                    state == "completed"
                        ? DeploymentDiagnosticSeverity.Information
                        : DeploymentDiagnosticSeverity.Error,
                    clock.GetUtcNow(),
                    $"\r\n[Docker Compose] Command {state}{(exitCode.HasValue ? $" (exit {exitCode})" : "")}.\r\n",
                    new DeploymentTerminalChannel(DeploymentTerminalChannelKind.Build), attributes,
                    SourceIdentity: new DeploymentDiagnosticSourceIdentity(DeploymentDiagnosticComponent.Compose,
                        DeploymentDiagnosticStream.Control, composeProject)), cancellationToken);
            }
            catch (Exception exception)
            {
                logger.LogWarning("Compose outcome diagnostic unavailable: {FailureType}.", exception.GetType().Name);
            }
        }
    }

    /// <summary>
    ///     Kills a process tree when the process was started and is still running.
    /// </summary>
    private static void KillProcessTree(Process process)
    {
        try
        {
            if (process.StartTime != default && !process.HasExited)
                process.Kill(true);
        }
        catch
        {
            // Process cleanup is best-effort because cancellation may race with natural process exit.
        }
    }
}