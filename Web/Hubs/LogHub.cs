using System.Security.Cryptography;
using Application.Abstractions.Diagnostics;
using Application.Data.Apps;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.SignalR;

namespace Web.Hubs;

/// <summary>
///     Authorizes project log subscriptions and manages project-specific SignalR groups.
/// </summary>
[AllowAnonymous]
public sealed class LogHub(
    IApplicationService applicationService,
    IDeploymentDiagnosticStore diagnosticStore,
    IDataProtectionProvider dataProtectionProvider,
    ILogger<LogHub> logger,
    IDeploymentRuntimeViewers viewers) : Hub<ILogClient>
{
    /// <summary>
    ///     Data Protection purpose shared with project details pages when generating log hub join tokens.
    /// </summary>
    internal const string ProtectorPurpose = "LogHub";

    /// <summary>
    ///     Allows a client to join a SignalR group associated with a specific project ID using a secure token.
    ///     This enables the client to receive real-time log updates related to the specified project securely.
    /// </summary>
    /// <param name="projectId">
    ///     The unique identifier of the project whose group the client wishes to join.
    /// </param>
    /// <param name="secureToken">
    ///     An encrypted token containing verified identity and project mappings to authenticate the connection.
    /// </param>
    /// <returns>A task representing the asynchronous operation.</returns>
    public async Task<DeploymentTerminalHistory> JoinProjectGroup(Guid projectId, Guid? deploymentId,
        string secureToken, long afterOrderId = 0)
    {
        var empty = new DeploymentTerminalHistory([], false);
        if (projectId == Guid.Empty || string.IsNullOrWhiteSpace(secureToken))
            throw new HubException("Invalid log subscription.");

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
            if (!deploymentId.HasValue) return empty;
            // Only a latest deployment can collect live output; historical subscriptions remain read-only.
            if (app.CsProjects.SelectMany(p => p.Deployments).MaxBy(d => d.CreatedAt)?.Id == deploymentId)
                viewers.Renew(Context.ConnectionId, projectId, deploymentId.Value);
            return afterOrderId > 0
                ? await diagnosticStore.ReadAfterAsync(projectId, deploymentId.Value, afterOrderId, 500,
                    Context.ConnectionAborted)
                : await diagnosticStore.ReadRecentAsync(projectId, deploymentId.Value, 500,
                    Context.ConnectionAborted);
        }
        catch (OperationCanceledException) when (Context.ConnectionAborted.IsCancellationRequested)
        {
            // A reload/navigation can disconnect while replay is awaiting storage. No replay cursor was confirmed.
            logger.LogDebug("Log replay canceled because the project connection closed.");
            return new DeploymentTerminalHistory([], false, CanAdvanceCursor: false);
        }
        catch (CryptographicException ex)
        {
            logger.LogDebug(ex, "Rejected log hub group join because the secure token was invalid or expired.");
            throw new HubException("Invalid log subscription.");
        }
    }


    /// <summary>
    ///     Allows a client to leave a SignalR group associated with a specific project ID.
    /// </summary>
    public async Task LeaveProjectGroup(Guid projectId)
    {
        if (projectId == Guid.Empty)
            return;

        viewers.Remove(Context.ConnectionId, projectId);
        await Groups.RemoveFromGroupAsync(Context.ConnectionId, GetProjectGroupName(projectId),
            Context.ConnectionAborted);
    }

    /// <inheritdoc />
    public override Task OnDisconnectedAsync(Exception? exception)
    {
        viewers.Remove(Context.ConnectionId);
        return base.OnDisconnectedAsync(exception);
    }


    /// <summary>
    ///     Generates the SignalR group name used for one project's log stream.
    /// </summary>
    internal static string GetProjectGroupName(Guid projectId)
    {
        return $"project-{projectId}";
    }
}