using Application.Abstractions.Diagnostics;
using Application.Abstractions.Logging;
using Application.Diagnostics;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Options;
using Web.Hubs;

namespace Web.Services;

/// <summary>
///     Streams deployment logs and container metrics to project-specific SignalR groups.
/// </summary>
/// <param name="hubContext">The SignalR hub context.</param>
/// <param name="options">Validated live transport deadline settings.</param>
/// <param name="redactor">Final text policy applied before every browser transport write.</param>
public sealed class RealTimeLogStreamer(
    IHubContext<LogHub> hubContext,
    IOptions<DeploymentDiagnosticOptions> options,
    IDiagnosticRedactor redactor) : ILogStreamer
{
    /// <summary>
    ///     Redacts resource identity and delivers bounded supported numeric samples to the project group.
    /// </summary>
    /// <param name="snapshot">Typed resource metrics with project, deployment and provider timestamp.</param>
    /// <param name="cancellationToken">Cancels the current read, request or delivery operation.</param>
    /// <returns>A task completing after the bounded SignalR write; transport failures propagate to the caller.</returns>
    /// <exception cref="ArgumentException">A required identity or setting is missing or invalid.</exception>
    /// <exception cref="TimeoutException">The live delivery write exceeds the configured deadline.</exception>
    /// <exception cref="OperationCanceledException">The supplied cancellation token cancels the delivery.</exception>
    public Task StreamDeploymentMetricsAsync(DeploymentRuntimeMetricSnapshot snapshot,
        CancellationToken cancellationToken = default)
    {
        ValidateProjectId(snapshot.ProjectId);
        if (snapshot.DeploymentId == Guid.Empty) throw new ArgumentException("Deployment identity is required.");
        var safe = snapshot with
        {
            ResourceName = redactor.RedactText(snapshot.ResourceName, 128),
            Samples = snapshot.Samples.Where(s => double.IsFinite(s.Value) && s.Value >= 0 &&
                                                  DeploymentMetricCatalog.Units.TryGetValue(s.Name, out var unit) &&
                                                  unit == s.Unit).Take(16).ToArray()
        };
        return SendAsync(snapshot.ProjectId, nameof(ILogClient.ReceiveDeploymentMetrics), [safe], cancellationToken);
    }

    /// <summary>
    ///     Redacts source-aware terminal output before project-group delivery.
    /// </summary>
    /// <param name="terminalLog">Redacted deployment output with channel and replay identity.</param>
    /// <param name="cancellationToken">Cancels the current read, request or delivery operation.</param>
    /// <returns>A task completing after the bounded SignalR write; transport failures propagate to the caller.</returns>
    /// <exception cref="ArgumentException">The project identity is empty or the terminal channel is blank.</exception>
    /// <exception cref="TimeoutException">The live delivery write exceeds the configured deadline.</exception>
    /// <exception cref="OperationCanceledException">The supplied cancellation token cancels the delivery.</exception>
    public Task StreamTerminalLogAsync(DeploymentTerminalLog terminalLog,
        CancellationToken cancellationToken = default)
    {
        ValidateProjectId(terminalLog.ProjectId);
        ArgumentException.ThrowIfNullOrWhiteSpace(terminalLog.TerminalChannel);

        var safe = redactor.RedactTerminal(terminalLog);
        return SendAsync(terminalLog.ProjectId, nameof(ILogClient.ReceiveTerminalLog), [safe],
            cancellationToken);
    }

    /// <summary>
    ///     Delivers redacted availability guidance separately from terminal output.
    /// </summary>
    /// <param name="projectId">The project identity used for authorization or correlation.</param>
    /// <param name="message">Display message; secret-bearing provider text must not be supplied.</param>
    /// <param name="cancellationToken">Cancels the current read, request or delivery operation.</param>
    /// <returns>A task completing after the bounded SignalR write; transport failures propagate to the caller.</returns>
    /// <exception cref="ArgumentException">The project identity is empty.</exception>
    /// <exception cref="TimeoutException">The live delivery write exceeds the configured deadline.</exception>
    /// <exception cref="OperationCanceledException">The supplied cancellation token cancels the delivery.</exception>
    public Task StreamTerminalNoticeAsync(Guid projectId, string message, CancellationToken cancellationToken = default)
    {
        ValidateProjectId(projectId);
        return SendAsync(projectId, nameof(ILogClient.ReceiveTerminalNotice), [redactor.RedactText(message)],
            cancellationToken);
    }

    /// <summary>
    ///     Delivers bounded redacted compatibility CPU and memory text to the project group.
    /// </summary>
    /// <param name="projectId">The project identity used for authorization or correlation.</param>
    /// <param name="containerName">Recorded container or resource identity.</param>
    /// <param name="cpuUsage">Compatibility CPU display text.</param>
    /// <param name="memoryUsage">Observed used/limit memory text.</param>
    /// <param name="cancellationToken">Cancels the current read, request or delivery operation.</param>
    /// <returns>A task completing after the bounded SignalR write; transport failures propagate to the caller.</returns>
    /// <exception cref="ArgumentException">The project identity is empty or the resource name is blank.</exception>
    /// <exception cref="TimeoutException">The live delivery write exceeds the configured deadline.</exception>
    /// <exception cref="OperationCanceledException">The supplied cancellation token cancels the delivery.</exception>
    public Task StreamContainerMetricsAsync(Guid projectId, string containerName, string cpuUsage,
        string memoryUsage, CancellationToken cancellationToken = default)
    {
        ValidateProjectId(projectId);
        ArgumentException.ThrowIfNullOrWhiteSpace(containerName);

        return SendAsync(projectId, nameof(ILogClient.ReceiveContainerMetrics),
        [
            redactor.RedactText(containerName, 128), redactor.RedactText(cpuUsage, 128),
            redactor.RedactText(memoryUsage, 128)
        ], cancellationToken);
    }

    /// <summary>
    ///     Cancels the actual SignalR write when its deadline expires, without leaving detached send
    ///     tasks.
    /// </summary>
    /// <param name="projectId">The project identity used for authorization or correlation.</param>
    /// <param name="method">Allowlisted SignalR client method name.</param>
    /// <param name="arguments">Redacted client callback arguments.</param>
    /// <param name="cancellationToken">Cancels the current read, request or delivery operation.</param>
    /// <returns>A task representing completion of this operation, including any awaited callbacks.</returns>
    /// <exception cref="TimeoutException">The live delivery write exceeds the configured deadline.</exception>
    /// <exception cref="OperationCanceledException">The supplied cancellation token cancels the delivery.</exception>
    private async Task SendAsync(Guid projectId, string method, object?[] arguments,
        CancellationToken cancellationToken)
    {
        using var activity = DeploymentTracing.Start(DeploymentOperation.SignalRSend, projectId);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(options.Value.DeliveryTimeoutSeconds));
        try
        {
            await hubContext.Clients.Group(LogHub.GetProjectGroupName(projectId))
                .SendCoreAsync(method, arguments, deadline.Token);
            DeploymentTracing.Finish(activity, DeploymentTraceOutcome.Completed);
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested &&
                                                 !cancellationToken.IsCancellationRequested)
        {
            DeploymentTracing.Finish(activity, DeploymentTraceOutcome.TimedOut);
            throw new TimeoutException("Live diagnostic delivery exceeded its deadline.");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            DeploymentTracing.Finish(activity, DeploymentTraceOutcome.Canceled);
            throw;
        }
        catch (Exception)
        {
            DeploymentTracing.Finish(activity, DeploymentTraceOutcome.Failed);
            throw;
        }
    }

    /// <summary>
    ///     Validates the project ID to prevent publishing to malformed SignalR group names.
    /// </summary>
    /// <param name="projectId">The project ID to validate.</param>
    /// <exception cref="ArgumentException">Thrown if the project ID is empty.</exception>
    private static void ValidateProjectId(Guid projectId)
    {
        if (projectId == Guid.Empty)
            throw new ArgumentException("Project id must not be empty.", nameof(projectId));
    }
}