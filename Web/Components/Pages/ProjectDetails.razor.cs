using System.Security.Claims;
using Core.DTO;
using Core.Entities;
using Core.Enums;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.SignalR.Client;
using Services.Data.Apps;
using Services.Data.Users;
using Services.Docker;
using Services.Orchestration;
using Services.Scanner;
using Web.Components.Shared;

namespace Web.Components.Pages;

/// <summary>
///     A Blazor component that displays the details of a specific project, including its name, description,
///     and other relevant information. This component retrieves the project details based on the provided
///     ProjectId parameter and ensures that the user is authenticated before displaying the information.
/// </summary>
public partial class ProjectDetails : ComponentBase, IAsyncDisposable
{
    /// A dictionary to store the latest CPU and memory usage metrics for each container.
    private readonly Dictionary<string, (string Cpu, string Memory)> _containerMetrics = new();

    /// A dictionary to store the terminal instances for each database container.
    private readonly Dictionary<string, Terminal> _dbTerminals = new();


    /// A list of container names for which metrics are currently being displayed.
    private readonly List<string> _metricContainerNames = [];

    /// A string to track the currently active tab in the UI, defaulting to "build".
    private string _activeTab = "build";


    /// A nullable variable to hold the app details fetched from the database.
    private Application? _app;

    /// A terminal instance for displaying build logs.
    private Terminal? _buildTerminal;

    /// A nullable variable to hold the current deployment configuration when the user initiates a deployment.
    private DeploymentConfigDto? _currentDeployConfig;

    /// The index of the currently displayed container's metrics in the list of container names.
    private int _currentMetricIndex;

    /// A list of database tabs to be displayed in the UI, initialized as an empty list.
    private IEnumerable<DatabaseTab> _databaseTabs = [];

    /// The port exposed by the web container.
    private int _exposedPort;

    /// A nullable variable to hold the SignalR hub connection for receiving real-time logs and metrics.
    private HubConnection? _hubConnection;


    /// A boolean flag to indicate whether the project is currently being deployed.
    private bool _isDeploying;

    /// A boolean flag to indicate whether the component is currently loading data.
    private bool _isLoading = true;

    /// A boolean flag to indicate whether the deployment process is currently being stopped.
    private bool _isStopping;

    /// A nullable variable to hold the path of the selected C# project when initiating a deployment.
    private string? _selectedProjectPath;

    /// A boolean flag to control the visibility of the deployment configuration modal.
    private bool _showConfigModal;

    /// A nullable variable to hold the terminal instance for displaying web container logs.
    private Terminal? _webTerminal;


    /// The ID of the project to be displayed, passed as a parameter to the component.
    [Parameter]
    public Guid ProjectId { get; set; }

    /// The project service used to fetch project details and manage project-related operations.
    [Inject]
    private IApplicationService ApplicationService { get; set; } = null!;

    /// The authentication state provider used to retrieve the current user's authentication state and claims.
    [Inject]
    private AuthenticationStateProvider AuthStateProvider { get; set; } = null!;

    /// The service scope factory used to create scopes for resolving scoped services during asynchronous operations.
    [Inject]
    private IServiceScopeFactory ScopeFactory { get; set; } = null!;

    /// The user service used to retrieve user details and manage user-related operations.
    [Inject]
    private IUserService UserService { get; set; } = null!;

    /// The navigation manager used to navigate between pages and handle URL changes.
    [Inject]
    private NavigationManager NavigationManager { get; set; } = null!;

    /// The data protection provider used to protect sensitive data.
    [Inject]
    private IDataProtectionProvider DataProtectionProvider { get; set; } = null!;

    /// The project scanner service used to analyze project dependencies and prepare deployment configurations.
    [Inject]
    private IProjectScannerService ProjectScanner { get; set; } = null!;

    /// The deployment orchestrator service used to manage Docker deployments.
    [Inject]
    private IDeploymentStatusNotifier DeploymentStatusNotifier { get; set; } = null!;

    /// The Docker service used to interact with the Docker daemon and manage Docker containers.
    [Inject]
    private IDockerService DockerService { get; set; } = null!;

    /// The logger used to log information and errors related to the project details component.
    [Inject]
    private ILogger<ProjectDetails> Logger { get; set; } = null!;


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

            await _hubConnection.DisposeAsync();
        }

        GC.SuppressFinalize(this);
    }


    /// <summary>
    ///     Determines whether a deployment is currently in progress.
    /// </summary>
    /// <returns>True if a deployment is in progress, otherwise false.</returns>
    private bool IsDeploying()
    {
        var status = GetLatestStatus();
        return _isDeploying || status == DeploymentStatus.Starting;
    }


    /// <summary>
    ///     Initiates the deployment process for the project by analyzing
    ///     its dependencies and preparing the deployment configuration.
    /// </summary>
    private async Task DeployProjectAsync()
    {
        if (_app == null) return;

        var csProject = _app.CsProjects.FirstOrDefault(p => p.IsWebProject);
        if (csProject == null) return;

        _selectedProjectPath = csProject.Path;
        _currentDeployConfig = await ProjectScanner.AnalyzeDependenciesAsync(_app, csProject);

        _showConfigModal = true;
    }


    /// <summary>
    ///     Stops the current deployment asynchronously.
    /// </summary>
    private async Task StopDeploymentAsync()
    {
        if (_app == null) return;
        var csProject = _app.CsProjects.FirstOrDefault(p => p.IsWebProject);
        if (csProject == null) return;

        _isStopping = true;
        StateHasChanged();

        _ = Task.Run(async () =>
        {
            using var scope = ScopeFactory.CreateScope();
            var orchestrator = scope.ServiceProvider.GetRequiredService<ILocalDeploymentOrchestrator>();

            try
            {
                await orchestrator.StopDeploymentAsync(ProjectId, _app.Name, csProject.Path);

                await InvokeAsync(() =>
                {
                    _isStopping = false;
                    StateHasChanged();
                });
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "Failed to stop deployment for project {ProjectId}", ProjectId);
            }
            finally
            {
                await InvokeAsync(() =>
                {
                    _isStopping = false;
                    StateHasChanged();
                });
            }
        });
    }


    /// <summary>
    ///     Hides the deployment configuration modal and resets the related state variables to their default values.
    /// </summary>
    private void HideConfigModal()
    {
        _showConfigModal = false;
        _currentDeployConfig = null;
        _selectedProjectPath = null;
    }


    /// <summary>
    ///     Executes the deployment process asynchronously by hiding the configuration modal,
    ///     setting the deploying state, and invoking the deployment orchestrator to deploy
    ///     the project with the specified configuration.
    /// </summary>
    /// <param name="finalConfig">The deployment configuration to be used for the project deployment.</param>
    private async Task ExecuteDeploymentAsync(DeploymentConfigDto finalConfig)
    {
        HideConfigModal();
        _isDeploying = true;
        StateHasChanged();

        _ = Task.Run(async () =>
        {
            try
            {
                using var scope = ScopeFactory.CreateScope();
                var orchestrator = scope.ServiceProvider.GetRequiredService<ILocalDeploymentOrchestrator>();
                await orchestrator.DeployLocalProjectAsync(finalConfig);
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "Failed to execute deployment for project {ProjectId}", finalConfig.ProjectId);
            }
            finally
            {
                await InvokeAsync(() =>
                {
                    _isDeploying = false;
                    StateHasChanged();
                });
            }
        });
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
            _app = await ApplicationService.GetAppByIdAsync(ProjectId, currentUserId);

            if (_app != null)
            {
                var csProject = _app.CsProjects.FirstOrDefault(csp => csp.IsWebProject);
                if (csProject != null)
                {
                    var config = await ProjectScanner.AnalyzeDependenciesAsync(_app, csProject);
                    _exposedPort = config.ExposedPort;
                    _databaseTabs = config.Databases
                        .Select(db =>
                            new DatabaseTab(db.DbType, db.ContainerNameSuffix, db.DbType))
                        .ToList();
                }
            }
        }

        await UpdateExposedPortAsync();

        _isLoading = false;
    }


    /// <summary>
    ///     Event handler that is called when the deployment status changes. It checks if
    ///     the status change is related to the current project, and if so, it updates the
    ///     latest deployment status in the project details and triggers a UI refresh.
    /// </summary>
    /// <param name="projectId">The ID of the project for which the status has changed.</param>
    /// <param name="status">The new deployment status.</param>
    private void OnDeploymentStatusChanged(Guid projectId, DeploymentStatus status)
    {
        if (_app != null && _app.Id == projectId)
        {
            var latestDeployment = _app.CsProjects
                .SelectMany(c => c.Deployments)
                .MaxBy(d => d.CreatedAt);

            if (latestDeployment != null)
            {
                latestDeployment.Status = status;
                _ = UpdateExposedPortAsync().ContinueWith(_ => InvokeAsync(StateHasChanged));
            }
            else
            {
                _ = RefreshProjectAsync();
            }
        }
    }


    /// <summary>
    ///     Updates the exposed port for the web container by checking the latest deployment status and retrieving
    ///     the host port mapped to the web container from the Docker service if the deployment is running.
    /// </summary>
    private async Task UpdateExposedPortAsync()
    {
        if (GetLatestStatus() == DeploymentStatus.Running && _app != null)
        {
            var csProject = _app.CsProjects.FirstOrDefault(csp => csp.IsWebProject);
            if (csProject != null)
            {
                var containerName = $"{csProject.Name.ToLowerInvariant()}-web";
                var hostPort = await DockerService.GetContainerHostPortAsync(containerName);
                if (hostPort > 0) _exposedPort = hostPort;
            }
        }
    }


    /// <summary>
    ///     Refreshes the project details by re-fetching the project from the database.
    /// </summary>
    private async Task RefreshProjectAsync()
    {
        var currentUserId = await GetCurrentUserIdAsync();
        if (currentUserId != Guid.Empty)
        {
            _app = await ApplicationService.GetAppByIdAsync(ProjectId, currentUserId);
            await UpdateExposedPortAsync();
            await InvokeAsync(StateHasChanged);
        }
    }


    /// <summary>
    ///     Retrieves the latest deployment status for the project by
    ///     looking at the most recent deployment across all C# projects.
    /// </summary>
    /// <returns></returns>
    private DeploymentStatus? GetLatestStatus()
    {
        return _app?.CsProjects
            .SelectMany(c => c.Deployments)
            .MaxBy(d => d.CreatedAt)?.Status;
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
            var protector = DataProtectionProvider.CreateProtector("LogHub").ToTimeLimitedDataProtector();
            var secureToken = protector.Protect($"{ProjectId}:{userId}", TimeSpan.FromMinutes(5));

            _hubConnection = new HubConnectionBuilder()
                .WithUrl(NavigationManager.ToAbsoluteUri("/loghub"))
                .WithAutomaticReconnect()
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

            _hubConnection.On<string, string, string>("ReceiveContainerMetrics",
                (containerName, cpuUsage, memoryUsage) =>
                {
                    InvokeAsync(() =>
                    {
                        _containerMetrics[containerName] = (cpuUsage, memoryUsage);
                        if (!_metricContainerNames.Contains(containerName)) _metricContainerNames.Add(containerName);
                        StateHasChanged();
                    });
                }
            );

            try
            {
                await _hubConnection.StartAsync();
                await _hubConnection.SendAsync("JoinProjectGroup", ProjectId, secureToken);
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "Failed to start SignalR connection for project {ProjectId}", ProjectId);
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


    /// Switches to the previous container's metrics.
    private void PreviousMetric()
    {
        if (_metricContainerNames.Count == 0) return;
        _currentMetricIndex = (_currentMetricIndex - 1 + _metricContainerNames.Count) % _metricContainerNames.Count;
    }


    /// Switches to the next container's metrics.
    private void NextMetric()
    {
        if (_metricContainerNames.Count == 0) return;
        _currentMetricIndex = (_currentMetricIndex + 1) % _metricContainerNames.Count;
    }


    /// <summary>
    ///     Retrieves the current authenticated user's ID from the authentication state.
    ///     If the user is not authenticated, it returns Guid.Empty.
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
            using var scope = ScopeFactory.CreateScope();
            var userService = scope.ServiceProvider.GetRequiredService<IUserService>();
            return await userService.GetUserIdByGithubAccountIdAsync(userIdString);
        }

        return Guid.Empty;
    }


    /// A record type representing a database tab in the UI.
    private record DatabaseTab(string Provider, string TabId, string DisplayName);
}