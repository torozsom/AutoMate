using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Services.LogStreaming;

namespace Services.Docker;

/// <summary>
///     Executes Docker CLI commands needed for Compose, metrics, port discovery, and project listing.
/// </summary>
internal sealed class DockerCli(DockerOptions options, ILogStreamer logStreamer, ILogger logger)
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
        CancellationToken cancellationToken, params string[] composeArguments)
    {
        var startInfo = DockerProcessStartInfoFactory.CreateCompose(workingDir, safeProjectName, composeArguments);
        return await ExecuteProcessStreamingLogsAsync(startInfo, projectId, cancellationToken);
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
                    process.ExitCode, error);
                return [];
            }

            return DockerComposeProjectParser.ParseRunningProjectNames(output);
        }
        catch (OperationCanceledException ex)
        {
            logger.LogWarning("[DockerService] 'docker compose ls' operation was cancelled or timed out. " +
                              "Exception: {Exception}", ex.Message);

            KillProcessTree(process);
            return [];
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "[DockerService] Error executing 'docker compose ls'.");
            return [];
        }
    }

    /// <summary>
    ///     Streams one container log line through the shared real-time log pipeline.
    /// </summary>
    public async Task StreamContainerLogAsync(Guid projectId, string containerSuffixOrTabId, string logLine)
    {
        await logStreamer.StreamContainerLogsAsync(projectId, containerSuffixOrTabId, logLine);
    }

    /// <summary>
    ///     Streams Docker CLI stats output for one container through the metrics channel.
    /// </summary>
    public async Task StreamContainerMetricsAsync(string containerName, Guid projectId, string containerSuffixOrTabId,
        CancellationToken cancellationToken)
    {
        try
        {
            logger.LogInformation("[DockerService] Starting to stream metrics for container '{ContainerName}'",
                containerName);

            var startInfo = DockerProcessStartInfoFactory.CreateDocker(false);
            startInfo.ArgumentList.Add("stats");
            startInfo.ArgumentList.Add(containerName);
            startInfo.ArgumentList.Add("--format");
            startInfo.ArgumentList.Add("{{.CPUPerc}}|{{.MemUsage}}");

            using var process = Process.Start(startInfo);
            if (process == null)
                return;

            await using var registration = cancellationToken.Register(() => KillProcessTree(process));

            using var reader = process.StandardOutput;
            while (!cancellationToken.IsCancellationRequested)
            {
                var line = await reader.ReadLineAsync(cancellationToken);
                if (line == null)
                    break;

                if (DockerMetricsLine.TryParse(line, out var metrics))
                    await logStreamer.StreamContainerMetricsAsync(projectId, containerSuffixOrTabId, metrics.Cpu,
                        metrics.Memory);
            }
        }
        catch (OperationCanceledException ex)
        {
            logger.LogInformation(
                "[DockerService] Stopped streaming metrics for container '{ContainerName}' (cancelled)." +
                "Exception: {Exception}.", containerName, ex.Message);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "[DockerService] Error streaming metrics for container '{ContainerName}'.",
                containerName);
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
            logger.LogWarning("[DockerService] Getting host port for container '{ContainerName}' was cancelled." +
                              "Exception: {Exception}.", containerName, ex.Message);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "[DockerService] Error getting host port for container '{ContainerName}'.",
                containerName);
        }

        return 0;
    }

    /// <summary>
    ///     Executes a process and forwards all emitted output to the deployment build log.
    /// </summary>
    private async Task<bool> ExecuteProcessStreamingLogsAsync(ProcessStartInfo startInfo, Guid projectId,
        CancellationToken cancellationToken)
    {
        using var process = new Process();
        process.StartInfo = startInfo;

        process.OutputDataReceived += (_, e) => StreamBuildLogLine(projectId, e.Data);
        process.ErrorDataReceived += (_, e) => StreamBuildLogLine(projectId, e.Data);

        try
        {
            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            using var timeoutCts = new CancellationTokenSource(TimeSpan.FromMinutes(options.ComposeTimeoutMinutes));
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);

            await process.WaitForExitAsync(linkedCts.Token);

            if (process.ExitCode != 0)
            {
                logger.LogError("[DockerService] Process failed with exit code {Code}. Command: {Cmd}",
                    process.ExitCode, DockerProcessStartInfoFactory.Describe(startInfo));
                return false;
            }

            return true;
        }
        catch (OperationCanceledException ex)
        {
            logger.LogError("[DockerService] Process timed out or was cancelled. Command: {Cmd}," +
                            " Exception: {Ex}", DockerProcessStartInfoFactory.Describe(startInfo), ex.Message);
            KillProcessTree(process);
            return false;
        }
        catch (Exception ex)
        {
            logger.LogCritical(ex, "[DockerService] Critical error launching process. Command: {Cmd}",
                DockerProcessStartInfoFactory.Describe(startInfo));
            return false;
        }
    }

    /// <summary>
    ///     Forwards a non-empty build log line to the real-time log streamer.
    /// </summary>
    private void StreamBuildLogLine(Guid projectId, string? line)
    {
        if (!string.IsNullOrWhiteSpace(line))
            logStreamer.StreamBuildLogsAsync(projectId, line + "\r\n");
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