using System.Security.Cryptography;
using Application.Abstractions.Diagnostics.Delivery;
using Application.Abstractions.Diagnostics.Events;
using Application.Abstractions.Diagnostics.History;
using Application.Data.Apps;
using Application.Diagnostics;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.SignalR;

namespace Web.Hubs;

/// <summary>
///     Authorizes project log subscriptions and manages project-specific SignalR groups.
/// </summary>
/// <param name="applicationService">Reads owner-scoped project metadata.</param>
/// <param name="diagnosticStore">Reads bounded persisted terminal history.</param>
/// <param name="dataProtectionProvider">Verifies time-limited project subscription tokens.</param>
/// <param name="logger">The logger used for fixed operational events without diagnostic payloads.</param>
/// <param name="viewers">Tracks expiring authorized browser subscriptions.</param>
[AllowAnonymous]
public sealed class LogHub(
    IApplicationService applicationService,
    IDeploymentDiagnosticStore diagnosticStore,
    IDataProtectionProvider dataProtectionProvider,
    ILogger<LogHub> logger,
    IDeploymentRuntimeViewers viewers) : Hub<ILogClient>
{
    /// <summary>Shared Data Protection purpose for short-lived owner/project subscription tokens.</summary>
    internal const string ProtectorPurpose = "LogHub";

    /// <summary>
    ///     Verifies the protected owner/project token, joins the project group and returns bounded
    ///     deployment replay.
    /// </summary>
    /// <param name="projectId">The unique identifier of the project whose group the client wishes to join.</param>
    /// <param name="deploymentId">Selected owned deployment for replay, or null to join without replay.</param>
    /// <param name="secureToken">
    ///     An encrypted token containing verified identity and project mappings to authenticate the
    ///     connection.
    /// </param>
    /// <param name="afterOrderId">Last acknowledged durable ordering position; zero requests recent output.</param>
    /// <returns>A task returning bounded replay output, or empty non-advancing history if the connection is canceled.</returns>
    /// <exception cref="HubException">
    ///     The subscription token is invalid, expired or does not authorize this
    ///     project/deployment.
    /// </exception>
    public async Task<DeploymentTerminalHistory> JoinProjectGroup(Guid projectId, Guid? deploymentId,
        string secureToken, long afterOrderId = 0)
    {
        using var activity = DeploymentTracing.Start(DeploymentOperation.SignalRJoin, projectId, deploymentId);
        using var correlation = OperationalLog.BeginCorrelation(logger, deploymentId, projectId: projectId);
        var empty = new DeploymentTerminalHistory([], false);
        if (projectId == Guid.Empty || string.IsNullOrWhiteSpace(secureToken))
        {
            DeploymentTracing.Finish(activity, DeploymentTraceOutcome.Denied);
            throw new HubException("Invalid log subscription.");
        }

        try
        {
            var protector = dataProtectionProvider.CreateProtector(ProtectorPurpose).ToTimeLimitedDataProtector();
            var payload = protector.Unprotect(secureToken);

            var parts = payload.Split(':');
            if (parts.Length != 2
                || !Guid.TryParse(parts[0], out var tokenProjectId)
                || !Guid.TryParse(parts[1], out var userId))
                throw new HubException("Invalid log subscription.");

            if (tokenProjectId != projectId)
                throw new HubException("Invalid log subscription.");

            var app = await applicationService.GetAppByIdAsync(projectId, userId, Context.ConnectionAborted);
            if (app is null)
                throw new HubException("Project log access denied.");
            if (deploymentId.HasValue && !app.CsProjects.SelectMany(project => project.Deployments)
                    .Any(deployment => deployment.Id == deploymentId.Value))
                throw new HubException("Deployment log access denied.");

            await Groups.AddToGroupAsync(Context.ConnectionId, GetProjectGroupName(projectId),
                Context.ConnectionAborted);
            if (!deploymentId.HasValue)
            {
                DeploymentTracing.Finish(activity, DeploymentTraceOutcome.Completed);
                return empty;
            }

            // Only a latest deployment can collect live output; historical subscriptions remain read-only.
            if (app.CsProjects.SelectMany(p => p.Deployments).MaxBy(d => d.CreatedAt)?.Id == deploymentId)
                viewers.Renew(Context.ConnectionId, projectId, deploymentId.Value);
            var history = await DeploymentTracing.RunAsync(DeploymentOperation.SignalRReplay, projectId, deploymentId,
                Context.ConnectionAborted, () => afterOrderId > 0
                    ? diagnosticStore.ReadAfterAsync(projectId, deploymentId.Value, afterOrderId, 500,
                        Context.ConnectionAborted)
                    : diagnosticStore.ReadRecentAsync(projectId, deploymentId.Value, 500, Context.ConnectionAborted));
            DeploymentTracing.Finish(activity, DeploymentTraceOutcome.Completed);
            return history;
        }
        catch (OperationCanceledException) when (Context.ConnectionAborted.IsCancellationRequested)
        {
            DeploymentTracing.Finish(activity, DeploymentTraceOutcome.Canceled);
            // A reload/navigation can disconnect while replay is awaiting storage. No replay cursor was confirmed.
            logger.LogDebug("Log replay canceled because the project connection closed.");
            return new DeploymentTerminalHistory([], false, CanAdvanceCursor: false);
        }
        catch (CryptographicException)
        {
            DeploymentTracing.Finish(activity, DeploymentTraceOutcome.Denied);
            logger.LogDebug("Rejected log hub group join because the secure token was invalid or expired.");
            throw new HubException("Invalid log subscription.");
        }
        catch (HubException)
        {
            DeploymentTracing.Finish(activity, DeploymentTraceOutcome.Denied);
            throw;
        }
        catch (Exception)
        {
            DeploymentTracing.Finish(activity, DeploymentTraceOutcome.Failed);
            throw;
        }
    }


    /// <summary>
    ///     Allows a client to leave a SignalR group associated with a specific project ID.
    /// </summary>
    /// <param name="projectId">The project identity used for authorization or correlation.</param>
    /// <returns>A task representing completion of this operation, including any awaited callbacks.</returns>
    public async Task LeaveProjectGroup(Guid projectId)
    {
        if (projectId == Guid.Empty)
            return;

        viewers.Remove(Context.ConnectionId, projectId);
        await DeploymentTracing.RunAsync(DeploymentOperation.SignalRLeave, projectId, null, Context.ConnectionAborted,
            () => Groups.RemoveFromGroupAsync(Context.ConnectionId, GetProjectGroupName(projectId),
                Context.ConnectionAborted));
    }

    /// <summary>
    ///     Removes this connection viewing leases before framework disconnect handling.
    /// </summary>
    /// <param name="exception">Connection failure, or null for a normal disconnect.</param>
    /// <returns>A task representing completion of this operation, including any awaited callbacks.</returns>
    public override Task OnDisconnectedAsync(Exception? exception)
    {
        viewers.Remove(Context.ConnectionId);
        return base.OnDisconnectedAsync(exception);
    }


    /// <summary>
    ///     Generates the SignalR group name used for one project's log stream.
    /// </summary>
    /// <param name="projectId">The project identity used for authorization or correlation.</param>
    /// <returns>The stable project-prefixed SignalR group name.</returns>
    internal static string GetProjectGroupName(Guid projectId)
    {
        return $"project-{projectId}";
    }
}
