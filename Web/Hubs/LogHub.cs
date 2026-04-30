using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;

namespace Web.Hubs;


/// <summary>
///     A SignalR hub that manages real-time communication for project logs. Clients can join or leave groups based on
///     project IDs, allowing them to receive log updates specific to the projects they are interested in.
/// </summary>
[AllowAnonymous]
public class LogHub : Hub
{
    /// Allows a client to join a SignalR group associated with a specific project ID.
    public async Task JoinProjectGroup(Guid projectId)
    {
        await Groups.AddToGroupAsync(Context.ConnectionId, $"project-{projectId}");
    }


    /// Allows a client to leave a SignalR group associated with a specific project ID.
    public async Task LeaveProjectGroup(Guid projectId)
    {
        await Groups.RemoveFromGroupAsync(Context.ConnectionId, $"project-{projectId}");
    }
}