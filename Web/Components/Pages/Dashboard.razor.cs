using System.Collections.Concurrent;
using Application.Abstractions.Hosting;
using Application.Abstractions.Scanning;
using Application.Data.Apps;
using Application.Data.Users;
using Application.Orchestration;
using Domain.DTO;
using Domain.Enums;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.JSInterop;
using Web.Components.Shared;

namespace Web.Components.Pages;

/// <summary>
///     Represents a dashboard component that displays user-specific project data.
///     This component is responsible for verifying user authentication and fetching
///     the associated projects based on the authenticated user's ID.
/// </summary>
public partial class Dashboard : ComponentBase, IDisposable
{
    /// A thread-safe dictionary containing the deployment states of projects.
    private readonly ConcurrentDictionary<Guid, bool> _deployingStates = new();

    /// <summary>Read lifetime cancelled on navigation.</summary>
    private readonly CancellationTokenSource _lifetime = new();

    /// The list of apps associated with the authenticated user, fetched from the database.
    private List<Domain.Entities.Application>? _apps;

    /// The Azure tenant ID entered for personal Microsoft account connections.
    private string _azureTenantId = string.Empty;

    /// The current deployment configuration being edited by the user, if any.
    private DeploymentConfigDto? _currentDeployConfig;

    /// The ID of the currently authenticated user, used to fetch and manage their projects.
    private Guid _currentUserId;

    /// <summary>Prevents notifications queued before navigation from updating a disposed component.</summary>
    private bool _disposed;

    /// A message to display global errors that occur during operations like deployment or project fetching.
    private string? _globalErrorMessage;

    /// A message to display global success notifications, such as successful deployments.
    private string? _globalSuccessMessage;

    /// <summary>Bounded inventory projection.</summary>
    private ProjectInventoryPage? _inventory;

    /// <summary>Fences superseded inventory replies.</summary>
    private int _inventoryVersion;

    /// A flag indicating whether the current user has connected an Azure account.
    private bool _isAzureConnected;

    /// A flag indicating whether the component is currently loading data, used to show loading indicators in the UI.
    private bool _isLoading = true;

    /// <summary>Stable retry key while one cloud submission is pending.</summary>
    private string? _pendingCloudIdempotencyKey;

    /// <summary>Project associated with the pending cloud retry key.</summary>
    private Guid _pendingCloudProjectId;

    /// <summary>Serializes deployment preparation before queue admission.</summary>
    private Guid? _preparingProject;

    /// <summary>Project awaiting a concrete removal confirmation.</summary>
    private Guid? _removeProject;

    /// <summary>Editable search text.</summary>
    private string _search = "";

    /// The remote application currently selected for cloud deployment.
    private Domain.Entities.Application? _selectedCloudApp;

    /// The file system path of the project currently selected for deployment configuration.
    private string? _selectedProjectPath;

    /// <summary>Connection dialog state.</summary>
    private bool _showAzureConnect;

    /// A flag indicating whether the deployment configuration modal is currently visible to the user.
    private bool _showConfigModal;

    /// <summary>Editable sort selection.</summary>
    private string _sort = "activity";

    /// <summary>Editable source filter.</summary>
    private string _source = "";

    /// <summary>Editable latest runtime filter.</summary>
    private string _status = "";

    /// <summary>Records fixed safe outcomes for asynchronous UI notification failures.</summary>
    [Inject]
    private ILogger<Dashboard> Logger { get; set; } = null!;


    /// Authentication State Provider for checking user authentication and retrieving user information.
    [Inject]
    private AuthenticationStateProvider AuthStateProvider { get; set; } = null!;

    /// Service for managing projects, including fetching, creating, and deleting projects associated with users.
    [Inject]
    private IApplicationService ApplicationService { get; set; } = null!;

    /// Queue that hands deployment work to the hosted background worker.
    [Inject]
    private IDeploymentJobQueue DeploymentJobQueue { get; set; } = null!;

    /// <summary>Durable SaaS cloud admission.</summary>
    [Inject]
    private ICloudDeploymentRunService CloudDeploymentRuns { get; set; } = null!;

    /// Service for managing user accounts.
    [Inject]
    private IUserService UserService { get; set; } = null!;

    /// Service responsible for scanning project files to extract metadata and analyze dependencies.
    [Inject]
    private IProjectScannerService ProjectScanner { get; set; } = null!;

    [Inject] private IDeploymentCapabilities DeploymentCapabilities { get; set; } = null!;

    /// Navigation manager for handling navigation within the application.
    [Inject]
    private NavigationManager NavigationManager { get; set; } = null!;

    /// Service responsible for notifying subscribers about changes in deployment statuses.
    [Inject]
    private IDeploymentStatusNotifier DeploymentStatusNotifier { get; set; } = null!;

    /// JS Runtime for interacting with the browser's JavaScript environment.
    [Inject]
    private IJSRuntime JSRuntime { get; set; } = null!;

    /// Azure connection error returned by the OAuth callback, if any.
    [Parameter]
    [SupplyParameterFromQuery(Name = "azure_error")]
    public string? AzureConnectionError { get; set; }

    /// <summary>Independent EF scopes prevent overlapping circuit read operations.</summary>
    [Inject]
    private IServiceScopeFactory ServiceScopes { get; set; } = null!;

    /// <summary>URL-backed search.</summary>
    [Parameter]
    [SupplyParameterFromQuery(Name = "q")]
    public string? Search { get; set; }

    /// <summary>URL-backed source filter.</summary>
    [Parameter]
    [SupplyParameterFromQuery(Name = "source")]
    public string? Source { get; set; }

    /// <summary>URL-backed status filter.</summary>
    [Parameter]
    [SupplyParameterFromQuery(Name = "status")]
    public string? Status { get; set; }

    /// <summary>URL-backed ordering.</summary>
    [Parameter]
    [SupplyParameterFromQuery(Name = "sort")]
    public string? Sort { get; set; }

    /// <summary>URL-backed page number.</summary>
    [Parameter]
    [SupplyParameterFromQuery(Name = "page")]
    public int? Page { get; set; }


    /// <summary>
    ///     Disposes of the component by unsubscribing from the deployment status change notifications.
    /// </summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _lifetime.Cancel();
        _lifetime.Dispose();
        if (AuthStateProvider is not null) AuthStateProvider.AuthenticationStateChanged -= OnAuthenticationChanged;
        DeploymentStatusNotifier.OnStatusChanged -= OnDeploymentStatusChanged;
        DeploymentJobQueue.StateChanged -= OnQueueStateChanged;
        GC.SuppressFinalize(this);
    }


    /// <summary>
    ///     On initialization, we check if the user is authenticated. If they are,
    ///     we attempt to retrieve their projects using their user ID. If the user ID
    ///     is not a valid GUID (which may be the case for GitHub users), we look up the
    ///     user in the database using their GitHub account ID and then retrieve their projects
    ///     using the internal user ID.
    ///     If the user is not authenticated, we simply set the loading state to false, which
    ///     will trigger the UI to show the appropriate message for unauthenticated users.
    /// </summary>
    protected override async Task OnInitializedAsync()
    {
        AuthStateProvider.AuthenticationStateChanged += OnAuthenticationChanged;
        DeploymentStatusNotifier.OnStatusChanged += OnDeploymentStatusChanged;
        DeploymentJobQueue.StateChanged += OnQueueStateChanged;

        _currentUserId = await GetCurrentUserIdAsync();

        if (_currentUserId != Guid.Empty)
        {
            _isAzureConnected = await UserService.HasAzureConnectionAsync(_currentUserId);
            await RefreshAppsAsync();
        }

        if (!string.IsNullOrWhiteSpace(AzureConnectionError))
            _globalErrorMessage = $"Azure connection failed: {AzureConnectionError}";

        _isLoading = false;
    }


    /// <summary>
    ///     Event handler that is called whenever the deployment status of an app changes. This method updates
    ///     the status of the latest deployment for the affected app in the UI. If no deployment record exists
    ///     yet for the app, it triggers a refresh of the apps list to ensure the UI reflects the status.
    /// </summary>
    /// <param name="appId">The unique identifier of the app whose deployment status has changed.</param>
    /// <param name="status">The new deployment status to be applied.</param>
    private void OnDeploymentStatusChanged(Guid appId, DeploymentStatus status)
    {
        _ = DispatchStatusChangedAsync(appId, status);
    }

    /// <summary>Marshals model changes and rendering together onto the circuit and observes notification failures.</summary>
    private async Task DispatchStatusChangedAsync(Guid appId, DeploymentStatus status)
    {
        if (_disposed) return;
        try
        {
            await InvokeAsync(async () =>
            {
                if (_disposed) return;
                await ApplyStatusChangedAsync(appId, status);
            });
        }
        catch (Exception) when (_disposed)
        {
            // Navigation can dispose the renderer after the notification was queued.
        }
        catch (Exception error)
        {
            Logger.LogWarning(
                "Deployment status subscriber failed for project {ProjectId} with status {Status}: {FailureType}.",
                appId, status, error.GetType().Name);
        }
    }

    /// <summary>Updates the current model and deploy controls only on the renderer dispatcher.</summary>
    private async Task ApplyStatusChangedAsync(Guid appId, DeploymentStatus status)
    {
        var app = _apps?.FirstOrDefault(p => p.Id == appId);
        if (app is null)
        {
            if (_inventory?.Items.Any(p => p.Id == appId) == true)
            {
                await RefreshAppsAsync();
                StateHasChanged();
            }

            return;
        }

        var latestDeployment = app.CsProjects
            .SelectMany(c => c.Deployments)
            .MaxBy(d => d.CreatedAt);

        if (latestDeployment is not null)
            latestDeployment.Status = status;
        else if (_inventory is null)
            await RefreshAppsAsync();

        if (status is DeploymentStatus.Running or DeploymentStatus.Failed or DeploymentStatus.Stopped)
            SetDeployingState(appId, false);

        if (_inventory is not null) await RefreshAppsAsync();
        StateHasChanged();
    }


    /// <summary>
    ///     Refreshes the list of apps for the current user.
    /// </summary>
    private async Task RefreshAppsAsync()
    {
        if (_currentUserId == Guid.Empty || _disposed) return;
        var version = ++_inventoryVersion;
        _isLoading = true;
        try
        {
            using var scope = ServiceScopes.CreateScope();
            var request = new ProjectInventoryRequest(_search,
                Enum.TryParse<SourceType>(_source, out var source) && Enum.IsDefined(source) ? source : null,
                Enum.TryParse<DeploymentStatus>(_status, out var status) && Enum.IsDefined(status) ? status : null,
                _sort, Page ?? 1);
            var result = await scope.ServiceProvider.GetRequiredService<IWorkspaceQuery>()
                .ProjectsAsync(_currentUserId, request, _lifetime.Token);
            if (!_disposed && version == _inventoryVersion) _inventory = result;
        }
        catch (OperationCanceledException) when (_disposed)
        {
        }
        catch (Exception)
        {
            if (!_disposed && version == _inventoryVersion)
                _globalErrorMessage = "Projects could not be loaded. Refresh to try again.";
        }
        finally
        {
            if (!_disposed && version == _inventoryVersion) _isLoading = false;
        }
    }


    /// <summary>
    ///     Deletes an app by its ID. This method first checks if the current user ID is valid.
    ///     If it is, it calls the app service to delete the project.
    /// </summary>
    /// <param name="appId"></param>
    private async Task DeleteAppAsync(Guid appId)
    {
        if (_currentUserId == Guid.Empty) return;

        ClearMessages();

        var success = await ApplicationService.DeleteAppAsync(appId, _currentUserId);

        if (success)
        {
            _apps?.RemoveAll(p => p.Id == appId);
            await RefreshAppsAsync();
        }
        else
        {
            _globalErrorMessage = "Failed to remove the project. It might have been already deleted.";
        }
    }


    /// <summary>
    ///     Initiates the deployment process for a given app. If the app is local,
    ///     it looks for a web project within the solution. If a web project is found, it analyzes
    ///     the app's dependencies to prepare the deployment configuration.
    /// </summary>
    /// <param name="app"></param>
    private async Task DeployAppAsync(Domain.Entities.Application app)
    {
        ClearMessages();

        if (app.SourceType == SourceType.Local && !DeploymentCapabilities.LocalDeploymentsEnabled)
        {
            _globalErrorMessage = "Local Docker deployments are available only in a self-hosted AutoMate installation.";
            return;
        }

        if (app.SourceType == SourceType.Remote)
        {
            if (!_isAzureConnected)
            {
                _globalErrorMessage = "Connect your Azure account before deploying GitHub projects to the cloud.";
                return;
            }

            _selectedCloudApp = app;
            _currentDeployConfig = CreateCloudDeploymentConfig(app);
            _selectedProjectPath = string.Empty;
            _showConfigModal = true;
            return;
        }

        var csProjectToDeploy = app.CsProjects.FirstOrDefault(csp => csp.IsWebProject);

        if (csProjectToDeploy is null)
        {
            _globalErrorMessage = $"No web project found in '{app.Name}' to deploy. Only web apps are supported.";
            return;
        }

        try
        {
            _currentDeployConfig = await ProjectScanner.AnalyzeDependenciesAsync(app, csProjectToDeploy);
            _selectedProjectPath = csProjectToDeploy.Path;
            _showConfigModal = true;
        }
        catch (Exception)
        {
            _globalErrorMessage = "Failed to analyze project dependencies. Verify the project files and try again.";
        }
    }


    /// <summary>
    ///     Cancels the deployment process by hiding the configuration modal
    ///     and clearing any selected project path or current deployment configuration.
    /// </summary>
    private void HideConfigModal()
    {
        _showConfigModal = false;
        _currentDeployConfig = null;
        _selectedProjectPath = null;
        _selectedCloudApp = null;
    }


    /// <summary>
    ///     Initiates the deployment process for a specified project configuration.
    ///     This method handles the deployment by invoking the local deployment orchestrator,
    ///     updating the UI state, and managing deployment success or failure messages.
    /// </summary>
    /// <param name="finalConfig">
    ///     The deployment configuration containing details such as project ID,
    ///     project name, environment settings, and database configuration.
    /// </param>
    /// <returns>
    ///     A task representing the asynchronous deployment operation.
    /// </returns>
    private async Task ExecuteDeploymentAsync(DeploymentConfigDto finalConfig)
    {
        var owner = _currentUserId;
        var cloudApp = _selectedCloudApp;
        HideConfigModal();
        ClearMessages();

        if (finalConfig.IsCloudDeployment)
        {
            if (cloudApp == null)
            {
                _globalErrorMessage = "The selected GitHub project could not be found for cloud deployment.";
                return;
            }

            if (!GitHubRepositoryUrlParser.TryParse(cloudApp.SourcePathOrUrl, out var repository))
            {
                _globalErrorMessage = "AutoMate could not determine the GitHub repository owner/name for this project.";
                return;
            }

            var azureCredentials = await UserService.GetAzureCloudCredentialsAsync(owner);
            if (_disposed || owner != _currentUserId) return;
            if (azureCredentials == null)
            {
                _globalErrorMessage = "Connect your Azure account before deploying GitHub projects to the cloud.";
                return;
            }

            var userDetails = await GetCurrentUserDetailsAsync();
            if (_disposed || owner != _currentUserId || userDetails.UserId != owner) return;
            if (string.IsNullOrWhiteSpace(userDetails.AccessToken))
            {
                _globalErrorMessage = "Connect your GitHub account before deploying GitHub projects to the cloud.";
                return;
            }

            SetDeployingState(finalConfig.ProjectId, true);

            try
            {
                if (!DeploymentCapabilities.LocalDeploymentsEnabled)
                {
                    if (_pendingCloudProjectId != finalConfig.ProjectId)
                    {
                        _pendingCloudProjectId = finalConfig.ProjectId;
                        _pendingCloudIdempotencyKey = null;
                    }

                    _pendingCloudIdempotencyKey ??= Guid.NewGuid().ToString("N");
                    await CloudDeploymentRuns.StartAsync(new CloudDeploymentStart(
                        owner, finalConfig.ProjectId, _pendingCloudIdempotencyKey,
                        repository.Owner, repository.Name, userDetails.AccessToken,
                        finalConfig, CloudDeploymentPageDefaults.CreateRemoteProjectMetadata(),
                        cloudApp.Name, "."));
                    _pendingCloudIdempotencyKey = null;
                }
                else
                {
                    await DeploymentJobQueue.EnqueueAsync(new CloudDeploymentJob(new CloudDeploymentRequestDto
                    {
                        RequestingUserId = owner,
                        Config = finalConfig,
                        Metadata = CloudDeploymentPageDefaults.CreateRemoteProjectMetadata(),
                        CsProjectName = cloudApp.Name,
                        RepositoryRoot = ".",
                        GitHubAccessToken = userDetails.AccessToken,
                        GitHubContainerRegistryToken = userDetails.AccessToken,
                        AzureCredentials = azureCredentials,
                        RepositoryOwner = repository.Owner,
                        RepositoryName = repository.Name
                    }));
                }

                if (_disposed || owner != _currentUserId) return;
                _globalSuccessMessage =
                    $"Cloud deployment workflow for '{finalConfig.ProjectName}' has been queued.";
            }
            catch (Exception)
            {
                _globalErrorMessage = "Cloud deployment failed to queue. Verify provider access and try again shortly.";
                SetDeployingState(finalConfig.ProjectId, false);
                return;
            }

            if (_disposed || owner != _currentUserId) return;
            await JSRuntime.InvokeVoidAsync("open", $"/project/{finalConfig.ProjectId}", "_blank");
            return;
        }

        SetDeployingState(finalConfig.ProjectId, true);

        try
        {
            await DeploymentJobQueue.EnqueueAsync(new LocalDeploymentJob(finalConfig));
            if (_disposed || owner != _currentUserId) return;
            _globalSuccessMessage = $"The '{finalConfig.ProjectName}' deployment has been queued.";
        }
        catch (Exception ex)
        {
            _globalErrorMessage = ex is InvalidOperationException &&
                                  ex.Message == "The deployment queue is full. Try again after a job starts."
                ? "The deployment queue is full. Try again after a job starts."
                : "AutoMate could not queue this deployment. Try again shortly.";
            SetDeployingState(finalConfig.ProjectId, false);
            return;
        }

        if (_disposed || owner != _currentUserId) return;
        await JSRuntime.InvokeVoidAsync("open", $"/project/{finalConfig.ProjectId}", "_blank");
    }


    /// <summary>
    ///     Resolves the current user's internal AutoMate ID from authentication claims.
    /// </summary>
    private async Task<Guid> GetCurrentUserIdAsync()
    {
        return await AuthenticatedUserResolver.GetCurrentUserIdAsync(AuthStateProvider, UserService);
    }


    /// <summary>
    ///     Gets the current user details, including the GitHub token for remote deployments.
    /// </summary>
    private async Task<(Guid UserId, string? AccessToken, bool IsGitHubUser)> GetCurrentUserDetailsAsync()
    {
        var details = await AuthenticatedUserResolver.GetCurrentUserDetailsAsync(AuthStateProvider, UserService);
        return (details.UserId, details.AccessToken, details.IsGitHubUser);
    }


    /// <summary>
    ///     Checks if a project is currently being deployed.
    /// </summary>
    private bool IsDeploying(Guid projectId)
    {
        var state = DeploymentJobQueue.GetProjectState(projectId);
        return _preparingProject == projectId || _deployingStates.GetValueOrDefault(projectId, false) ||
               state.QueuedDeployments > 0 || state.ActiveDeployments > 0;
    }

    private void OnQueueStateChanged(Guid projectId)
    {
        if (!_disposed && (_inventory?.Items.Any(app => app.Id == projectId) == true ||
                           _apps?.Any(app => app.Id == projectId) == true)) _ = InvokeAsync(StateHasChanged);
    }


    /// <summary>
    ///     Determines whether a project card's deploy action should be disabled.
    /// </summary>
    private bool IsDeployDisabled(Domain.Entities.Application app)
    {
        return IsDeploying(app.Id) ||
               (app.SourceType == SourceType.Local && !DeploymentCapabilities.LocalDeploymentsEnabled) ||
               (app.SourceType == SourceType.Remote && !_isAzureConnected);
    }


    /// <summary>
    ///     Gets a short tooltip explaining why a remote deploy action is disabled.
    /// </summary>
    private string GetDeployButtonTitle(Domain.Entities.Application app)
    {
        return app.SourceType == SourceType.Remote && !_isAzureConnected
            ? "Connect to Azure to deploy GitHub projects."
            : "Deploy project";
    }


    /// <summary>
    ///     Creates a cloud deployment configuration for a saved remote repository.
    /// </summary>
    private static DeploymentConfigDto CreateCloudDeploymentConfig(Domain.Entities.Application app)
    {
        return CloudDeploymentPageDefaults.CreateConfiguration(app);
    }


    /// <summary>
    ///     Starts the tenant-specific Azure OAuth flow for personal Microsoft account tenants.
    /// </summary>
    private void ConnectPersonalAzureAccount()
    {
        ClearMessages();

        var tenantId = _azureTenantId.Trim();
        if (!Guid.TryParse(tenantId, out _))
        {
            _globalErrorMessage = "Enter a valid Azure tenant ID before connecting a personal Azure account.";
            return;
        }

        NavigationManager.NavigateTo(
            $"/api/auth/azure-login?tenantId={Uri.EscapeDataString(tenantId)}",
            true);
    }


    /// <summary>
    ///     Gets the latest deployment status of an app.
    /// </summary>
    private static DeploymentStatus? GetLatestStatus(Domain.Entities.Application app)
    {
        return app.CsProjects
            .SelectMany(c => c.Deployments)
            .MaxBy(d => d.CreatedAt)?.Status;
    }


    /// <summary>
    ///     Sets the deploying state for a specific app.
    /// </summary>
    /// <param name="appId">The ID of the app.</param>
    /// <param name="isDeploying">Indicates whether the project is currently deploying.</param>
    private void SetDeployingState(Guid appId, bool isDeploying)
    {
        if (isDeploying)
            _deployingStates[appId] = true;
        else
            _deployingStates.TryRemove(appId, out _);

        StateHasChanged();
    }


    /// <summary>
    ///     Clears any global error or success messages. This is typically called before starting
    ///     a new operation to ensure that old messages do not persist and confuse the user.
    /// </summary>
    private void ClearMessages()
    {
        _globalErrorMessage = null;
        _globalSuccessMessage = null;
    }


    /// <summary>
    ///     Navigates the user to the project details page for a specific project.
    /// </summary>
    /// <param name="appId">The ID of the project to navigate to.</param>
    private void NavigateToProject(Guid appId)
    {
        NavigationManager.NavigateTo($"/project/{appId}");
    }

    /// <inheritdoc />
    protected override async Task OnParametersSetAsync()
    {
        _search = Search ?? "";
        _source = Source ?? "";
        _status = Status ?? "";
        _sort = Sort is "name" or "saved" ? Sort : "activity";
        await RefreshAppsAsync();
    }

    /// <summary>Encodes a stable filter URL for pagination and browser history.</summary>
    private string InventoryUrl(int page)
    {
        return
            $"/dashboard?q={Uri.EscapeDataString(_search)}&source={Uri.EscapeDataString(_source)}&status={Uri.EscapeDataString(_status)}&sort={_sort}&page={Math.Max(1, page)}";
    }

    /// <summary>Applies filters through navigation and resets to the first page.</summary>
    private void ApplyFilters()
    {
        NavigationManager.NavigateTo(InventoryUrl(1));
    }

    /// <summary>Loads only the selected owned entity graph when an action needs it.</summary>
    private async Task DeployInventoryAsync(Guid id)
    {
        if (_disposed || _preparingProject is not null) return;
        _preparingProject = id;
        var owner = _currentUserId;
        try
        {
            using var scope = ServiceScopes.CreateScope();
            var app = await scope.ServiceProvider.GetRequiredService<IApplicationService>()
                .GetAppByIdAsync(id, owner, _lifetime.Token);
            if (_disposed || owner != _currentUserId) return;
            if (app is null)
            {
                _globalErrorMessage = "This project is no longer available.";
                return;
            }

            _apps = [app];
            await DeployAppAsync(app);
        }
        catch (OperationCanceledException) when (_disposed)
        {
        }
        catch (Exception)
        {
            if (!_disposed) _globalErrorMessage = "Deployment settings could not be prepared. Try again shortly.";
        }
        finally
        {
            _preparingProject = null;
        }
    }

    /// <summary>Opens the project removal confirmation.</summary>
    private void ConfirmRemoval(Guid id)
    {
        _removeProject = id;
    }

    /// <summary>Dismisses project removal.</summary>
    private void CancelRemoval()
    {
        _removeProject = null;
    }

    /// <summary>Removes the explicitly selected owned project and reloads its page.</summary>
    private async Task RemoveConfirmedAsync(Guid id)
    {
        _removeProject = null;
        await DeleteAppAsync(id);
    }

    /// <summary>Dismisses the connection dialog.</summary>
    private void CloseAzureDialog()
    {
        _showAzureConnect = false;
    }

    /// <summary>Clears project state when authentication changes on a still-open circuit.</summary>
    private void OnAuthenticationChanged(Task<AuthenticationState> state)
    {
        _ = RebindOwnerAsync();
    }

    /// <summary>Fences old-owner replies and closes configuration before binding the new account.</summary>
    private async Task RebindOwnerAsync()
    {
        if (_disposed) return;
        try
        {
            await InvokeAsync(async () =>
            {
                if (_disposed) return;
                var version = ++_inventoryVersion;
                _currentUserId = Guid.Empty;
                _inventory = null;
                _apps = null;
                _isAzureConnected = false;
                _removeProject = null;
                _showAzureConnect = false;
                _pendingCloudIdempotencyKey = null;
                _pendingCloudProjectId = Guid.Empty;
                _deployingStates.Clear();
                HideConfigModal();
                ClearMessages();
                StateHasChanged();
                using var scope = ServiceScopes.CreateScope();
                var users = scope.ServiceProvider.GetRequiredService<IUserService>();
                var owner = await AuthenticatedUserResolver.GetCurrentUserIdAsync(AuthStateProvider, users,
                    _lifetime.Token);
                var connected = owner != Guid.Empty && await users.HasAzureConnectionAsync(owner, _lifetime.Token);
                if (_disposed || version != _inventoryVersion) return;
                _currentUserId = owner;
                _isAzureConnected = connected;
                await RefreshAppsAsync();
                if (!_disposed) StateHasChanged();
            });
        }
        catch (OperationCanceledException) when (_disposed)
        {
        }
        catch (Exception error)
        {
            if (!_disposed)
                Logger.LogWarning("Workspace identity refresh failed: {FailureType}.", error.GetType().Name);
        }
    }
}