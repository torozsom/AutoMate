using System.Security.Cryptography;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.SignalR;
using Services.Data.Projects;

namespace Web.Hubs;

/// <summary>
///     A SignalR hub that manages real-time communication for project logs. Clients can join or leave groups based on
///     project IDs, allowing them to receive log updates specific to the projects they are interested in.
/// </summary>
[AllowAnonymous]
public class LogHub(IServiceProvider serviceProvider, IDataProtectionProvider dataProtectionProvider) : Hub<ILogClient>
{
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
        try
        {
            var protector = dataProtectionProvider.CreateProtector("LogHub").ToTimeLimitedDataProtector();
            var unprotect = protector.Unprotect(secureToken);

            var parts = unprotect.Split(':');
            if (parts.Length != 2
                || !Guid.TryParse(parts[0], out var tokenProjectId)
                || !Guid.TryParse(parts[1], out var userId))
                return;

            if (tokenProjectId != projectId) return;

            using var scope = serviceProvider.CreateScope();
            var projectService = scope.ServiceProvider.GetRequiredService<IProjectService>();

            var project = await projectService.GetProjectByIdAsync(projectId, userId);
            if (project != null)
                await Groups.AddToGroupAsync(Context.ConnectionId, $"project-{projectId}");
        }
        catch (CryptographicException ex)
        {
            Console.WriteLine($"Failed to join project group due to invalid token: {ex.Message}");
        }
    }


    /// Allows a client to leave a SignalR group associated with a specific project ID.
    public async Task LeaveProjectGroup(Guid projectId)
    {
        await Groups.RemoveFromGroupAsync(Context.ConnectionId, $"project-{projectId}");
    }
}