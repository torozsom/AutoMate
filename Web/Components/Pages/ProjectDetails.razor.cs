using Core.Entities;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.SignalR.Client;
using Services.Projects;
using System.Security.Claims;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Services.Data;

namespace Web.Components.Pages;

/// <summary>
///     A Blazor component that displays the details of a specific project, including its name, description,
///     and other relevant information. This component retrieves the project details based on the provided
///     ProjectId parameter and ensures that the user is authenticated before displaying the information.
/// </summary>
public partial class ProjectDetails : ComponentBase, IAsyncDisposable
{
    [Parameter]
    public Guid ProjectId { get; set; }

    [Inject]
    private IProjectService ProjectService { get; set; } = null!;

    [Inject]
    private AuthenticationStateProvider AuthStateProvider { get; set; } = null!;

    [Inject]
    private IServiceProvider ServiceProvider { get; set; } = null!;

    [Inject]
    private NavigationManager NavigationManager { get; set; } = null!;

    [Inject]
    private IDataProtectionProvider DataProtectionProvider { get; set; } = null!;

    private Project? _project;
    private bool _isLoading = true;

    private Terminal? _buildTerminal;

    private HubConnection? _hubConnection;


    /// <summary>
    ///     Initializes the component by retrieving the current authenticated user's ID and fetching the project details
    ///     based on the provided ProjectId. If the user is not authenticated, it will not attempt to fetch the project
    ///     and will simply set the loading state to false.
    /// </summary>
    protected override async Task OnInitializedAsync()
    {
        var currentUserId = await GetCurrentUserIdAsync();

        if (currentUserId != Guid.Empty)
            _project = await ProjectService.GetProjectByIdAsync(ProjectId, currentUserId);

        _isLoading = false;
    }


    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (firstRender)
        {
            var userId = await GetCurrentUserIdAsync();
            var protector = DataProtectionProvider.CreateProtector("LogHub");
            var secureToken = protector.Protect($"{ProjectId}:{userId}");

            _hubConnection = new HubConnectionBuilder()
                .WithUrl(NavigationManager.ToAbsoluteUri("/loghub"))
                .Build();

            _hubConnection.On<string>("ReceiveBuildLog", async (message) =>
            {
                if (_buildTerminal != null)
                {
                    await _buildTerminal.WriteAsync(message);
                }
            });

            try
            {
                await _hubConnection.StartAsync();
                await _hubConnection.SendAsync("JoinProjectGroup", ProjectId, secureToken);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"SignalR Connection Error: {ex.Message}");
            }
        }
    }


    /// <summary>
    ///     This method is called when the terminal component is ready. It writes an initial message to the terminal
    ///     indicating that the AutoMate Terminal has been initialized and is waiting for deployment logs.
    /// </summary>
    private async Task OnTerminalReady()
    {
        if (_buildTerminal != null)
        {
            await _buildTerminal.WriteLineAsync("\x1b[1;32mAutoMate Terminal Initialized...\x1b[0m");
            await _buildTerminal.WriteLineAsync("Waiting for deployment logs...");
            await _buildTerminal.WriteAsync("$ ");
        }
    }


    /// <summary>
    ///     Retrieves the current authenticated user's ID from the authentication state. If the user is not authenticated,
    ///     it returns Guid.Empty. It also includes a fallback mechanism to handle GitHub users by checking the database
    ///     for a matching GitHub user based on the account ID.
    /// </summary>
    /// <returns>The user ID if authenticated, otherwise Guid.Empty.</returns>
    private async Task<Guid> GetCurrentUserIdAsync()
    {
        var authState = await AuthStateProvider.GetAuthenticationStateAsync();
        var user = authState.User;

        if (!user.Identity?.IsAuthenticated ?? true)
            return Guid.Empty;

        var userIdString = user.FindFirst(ClaimTypes.NameIdentifier)?.Value;

        if (Guid.TryParse(userIdString, out var parsedId))
            return parsedId;

        // Fallback for GitHub users
        if (!string.IsNullOrEmpty(userIdString))
        {
            using var scope = ServiceProvider.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AutoMateDbContext>();
            var dbUser = await db.Users
                .OfType<GitHubUser>()
                .FirstOrDefaultAsync(u => u.AccountId == userIdString);

            return dbUser?.Id ?? Guid.Empty;
        }

        return Guid.Empty;
    }


    /// <summary>
    ///     Disposes of the component by leaving the SignalR group
    ///     associated with the project and disposing of the hub connection.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        if (_hubConnection is not null)
        {
            if (_hubConnection.State == HubConnectionState.Connected)
            {
                await _hubConnection.SendAsync("LeaveProjectGroup", ProjectId);
            }
            GC.SuppressFinalize(this);
        }
    }
}