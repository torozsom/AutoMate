using System.Security.Claims;
using Core.Entities;
using Core.Enums;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.EntityFrameworkCore;
using Services.Data;
using Services.Projects;
using Services.Scanner;
using Services.Orchestration;
using Web.Components.Shared;

namespace Web.Components.Pages;

/// <summary>
///     A Blazor component that displays the details of a specific project, including its name, description,
///     and other relevant information. This component retrieves the project details based on the provided
///     ProjectId parameter and ensures that the user is authenticated before displaying the information.
/// </summary>
public partial class ProjectDetails : ComponentBase, IAsyncDisposable
{
    private readonly Dictionary<string, Terminal> _dbTerminals = new();

    private string _activeTab = "build";

    private Terminal? _buildTerminal;
    private IEnumerable<DatabaseTab> _databaseTabs = [];

    private HubConnection? _hubConnection;
    private bool _isLoading = true;

    private Project? _project;
    private Terminal? _webTerminal;

    [Parameter] public Guid ProjectId { get; set; }

    [Inject] private IProjectService ProjectService { get; set; } = null!;

    [Inject] private AuthenticationStateProvider AuthStateProvider { get; set; } = null!;

    [Inject] private IServiceProvider ServiceProvider { get; set; } = null!;

    [Inject] private NavigationManager NavigationManager { get; set; } = null!;

    [Inject] private IDataProtectionProvider DataProtectionProvider { get; set; } = null!;

    [Inject] private IProjectScannerService ProjectScanner { get; set; } = null!;
    
    [Inject] private IDeploymentStatusNotifier DeploymentStatusNotifier { get; set; } = null!;


    /// <summary>
    ///     Disposes of the component by leaving the SignalR group
    ///     associated with the project and disposing of the hub connection.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        DeploymentStatusNotifier.OnStatusChanged -= OnDeploymentStatusChanged;

        if (_hubConnection is not null)
        {
            if (_hubConnection.State == HubConnectionState.Connected)
                await _hubConnection.SendAsync("LeaveProjectGroup", ProjectId);
            GC.SuppressFinalize(this);
        }
    }


    /// <summary>
    ///     Asynchronously initializes the component by retrieving the current user's ID and fetching the project details
    ///     based on the provided ProjectId. If the project is found, it analyzes the project's dependencies to determine
    ///     the databases used and prepares the database tabs for display.
    /// </summary>
    protected override async Task OnInitializedAsync()
    {
        DeploymentStatusNotifier.OnStatusChanged += OnDeploymentStatusChanged;
        var currentUserId = await GetCurrentUserIdAsync();

        if (currentUserId != Guid.Empty)
        {
            _project = await ProjectService.GetProjectByIdAsync(ProjectId, currentUserId);

            if (_project != null)
            {
                var csProject = _project.CsProjects.FirstOrDefault(csp => csp.IsWebProject);
                if (csProject != null)
                {
                    var config = await ProjectScanner.AnalyzeDependenciesAsync(_project, csProject);
                    _databaseTabs = config.Databases
                        .Select(db =>
                            new DatabaseTab(db.DbType, db.ContainerNameSuffix, db.DbType))
                        .ToList();
                }
            }
        }

        _isLoading = false;
    }
    
    private void OnDeploymentStatusChanged(Guid projectId, DeploymentStatus status)
    {
        if (_project != null && _project.Id == projectId)
        {
            var latestDeployment = _project.CsProjects
                .SelectMany(c => c.Deployments)
                .OrderByDescending(d => d.CreatedAt)
                .FirstOrDefault();

            if (latestDeployment != null)
            {
                latestDeployment.Status = status;
                InvokeAsync(StateHasChanged);
            }
            else
            {
                _ = RefreshProjectAsync();
            }
        }
    }
    
    private async Task RefreshProjectAsync()
    {
        var currentUserId = await GetCurrentUserIdAsync();
        if (currentUserId != Guid.Empty)
        {
            _project = await ProjectService.GetProjectByIdAsync(ProjectId, currentUserId);
            await InvokeAsync(StateHasChanged);
        }
    }


    private DeploymentStatus? GetLatestStatus()
    {
        return _project?.CsProjects
            .SelectMany(c => c.Deployments)
            .OrderByDescending(d => d.CreatedAt)
            .FirstOrDefault()?.Status;
    }

    /// Sets the active tab in the UI based on the provided tab ID.
    private void SetActiveTab(string tabId)
    {
        _activeTab = tabId;
    }


    /// Retrieves the CSS class for a tab based on whether it is the active tab or not.
    private string GetTabClass(string tabId)
    {
        return _activeTab == tabId
            ? "text-light bg-dark border-secondary border-opacity-50 active"
            : "text-secondary bg-transparent border-0";
    }


    /// Retrieves the inline style for a terminal based on whether its corresponding tab is active or not.
    private string GetTerminalStyle(string tabId)
    {
        return _activeTab == tabId
            ? "position: absolute; inset: 0; z-index: 1; visibility: visible;"
            : "position: absolute; inset: 0; z-index: 0; visibility: hidden;";
    }


    /// Retrieves the list of database tabs to be displayed in the UI.
    private IEnumerable<DatabaseTab> GetDatabaseTabs()
    {
        return _databaseTabs;
    }


    /// <summary>
    ///     Executes logic after the component has been rendered. On the first render, establishes a SignalR connection
    ///     to the server for real-time updates and joins a project-specific group using a secure token. Registers handlers
    ///     for receiving build and container logs. This method is only executed during the first render to set up necessary
    ///     resources for the project details page.
    /// </summary>
    /// <param name="firstRender">Indicates whether this is the first time the component is being rendered.</param>
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


            _hubConnection.On<string>("ReceiveBuildLog", async message =>
            {
                if (_buildTerminal != null)
                    await _buildTerminal.WriteAsync(message);
            });

            _hubConnection.On<string, string>("ReceiveContainerLog", async (containerIdentifier, message) =>
            {
                if (containerIdentifier == "web" && _webTerminal != null)
                    await _webTerminal.WriteAsync(message);
                else if (_dbTerminals.TryGetValue(containerIdentifier, out var dbTerminal))
                    await dbTerminal.WriteAsync(message);
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
    private static async Task OnTerminalReady(Terminal? terminal, string componentName)
    {
        if (terminal != null)
        {
            await terminal.WriteLineAsync($"\x1b[1;32mAutoMate {componentName} Terminal Initialized...\x1b[0m");
            await terminal.WriteLineAsync("Waiting for logs...");
            await terminal.WriteAsync("$ ");
        }
    }


    /// <summary>
    ///     This method is called when the build terminal component is ready. It calls the
    ///     OnTerminalReady method with the build terminal instance and the component name "Build".
    /// </summary>
    /// <param name="tabId"></param>
    /// <param name="dbType"></param>
    private async Task OnDbTerminalReady(string tabId, string dbType)
    {
        if (_dbTerminals.TryGetValue(tabId, out var terminal))
            await OnTerminalReady(terminal, dbType);
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

    /// A record type representing a database tab in the UI.
    private record DatabaseTab(string Provider, string TabId, string DisplayName);
}