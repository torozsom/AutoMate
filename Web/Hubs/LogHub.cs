using System.Security.Cryptography;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.SignalR;
using Services.Data.Apps;

namespace Web.Hubs;

/// <summary>
///     A SignalR hub that manages real-time communication for project logs. Clients can join or leave groups based on
///     project IDs, allowing them to receive log updates specific to the projects they are interested in.
/// </summary>
[AllowAnonymous]
public class LogHub(
    IApplicationService applicationService,
    IDataProtectionProvider dataProtectionProvider,
    ILogger<LogHub> logger) : Hub<ILogClient>
{
    private const string ProtectorPurpose = "LogHub";

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
    public async Task JoinProjectGroup(Guid projectId, string secureToken)
    {
        if (projectId == Guid.Empty || string.IsNullOrWhiteSpace(secureToken))
            return;

        try
        {
            var protector = dataProtectionProvider.CreateProtector(ProtectorPurpose).ToTimeLimitedDataProtector();
            var payload = protector.Unprotect(secureToken);

            var parts = payload.Split(':');
            if (parts.Length != 2
                || !Guid.TryParse(parts[0], out var tokenProjectId)
                || !Guid.TryParse(parts[1], out var userId))
                return;

            if (tokenProjectId != projectId)
                return;

            var app = await applicationService.GetAppByIdAsync(projectId, userId, Context.ConnectionAborted);
            if (app != null)
                await Groups.AddToGroupAsync(Context.ConnectionId, GetProjectGroupName(projectId), Context.ConnectionAborted);
        }
        catch (CryptographicException ex)
        {
            logger.LogDebug(ex, "Rejected log hub group join because the secure token was invalid or expired.");
        }
    }


    /// Allows a client to leave a SignalR group associated with a specific project ID.
    public async Task LeaveProjectGroup(Guid projectId)
    {
        if (projectId == Guid.Empty)
            return;

        await Groups.RemoveFromGroupAsync(Context.ConnectionId, GetProjectGroupName(projectId), Context.ConnectionAborted);
    }


    /// Generates a consistent group name for a given project ID, which is used to manage client subscriptions to log updates for that project.
    internal static string GetProjectGroupName(Guid projectId) => $"project-{projectId}";
}
