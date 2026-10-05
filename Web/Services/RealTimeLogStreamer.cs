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
    /// <inheritdoc />
    public Task StreamTerminalLogAsync(DeploymentTerminalLog terminalLog,
        CancellationToken cancellationToken = default)
    {
        ValidateProjectId(terminalLog.ProjectId);
        ArgumentException.ThrowIfNullOrWhiteSpace(terminalLog.TerminalChannel);

        var safe = redactor.RedactTerminal(terminalLog);
        return SendAsync(terminalLog.ProjectId, nameof(ILogClient.ReceiveTerminalLog), [safe],
            cancellationToken);
    }

    /// <inheritdoc />
    public Task StreamTerminalNoticeAsync(Guid projectId, string message, CancellationToken cancellationToken = default)
    {
        ValidateProjectId(projectId);
        return SendAsync(projectId, nameof(ILogClient.ReceiveTerminalNotice), [redactor.RedactText(message)],
            cancellationToken);
    }

    /// <inheritdoc />
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

    /// <summary>Cancels the actual SignalR write when its deadline expires, without leaving detached send tasks.</summary>
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