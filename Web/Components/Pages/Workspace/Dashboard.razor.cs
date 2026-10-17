using System.Collections.Concurrent;
using Application.Abstractions.Hosting;
using Application.Abstractions.Scanning;
using Application.Data.Apps;
using Application.Data.Users;
using Application.Orchestration.Jobs;
using Application.Orchestration.Status;
using Domain.DTO.Deployment;
using Domain.Enums.Deployments;
using Domain.Enums.Projects;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.JSInterop;
using Web.Components.Pages.Identity;
using Web.Components.Pages.Projects;
using Web.Components.Shared.Projects;
using Web.Services;

namespace Web.Components.Pages.Workspace;

/// <summary>
///     Represents a dashboard component that displays user-specific project data. This component is
///     responsible for verifying user authentication and fetching the associated projects based on
///     the authenticated user's ID.
/// </summary>
public partial class Dashboard : ComponentBase, IDisposable
{
    /// <summary>Per-project deployment-admission state.</summary>
    private readonly ConcurrentDictionary<Guid, bool> _deployingStates = new();

    /// <summary>Read lifetime cancelled on navigation.</summary>
    private readonly CancellationTokenSource _lifetime = new();

    /// <summary>Prevents duplicate stop requests while loading the owned project.</summary>
    private readonly HashSet<Guid> _preparingStops = [];

    /// <summary>Current owner project inventory, or null before loading.</summary>
    private List<Domain.Entities.Projects.Application>? _apps;

    /// <summary>Tenant identity entered for Azure account connection.</summary>
    private string _azureTenantId = string.Empty;

    /// <summary>User-editable deployment configuration for the open form.</summary>
    private DeploymentConfigDto? _currentDeployConfig;

    /// <summary>Resolved internal owner identity.</summary>
    private Guid _currentUserId;

    /// <summary>Prevents notifications queued before navigation from updating a disposed component.</summary>
    private bool _disposed;

    /// <summary>Safe page-level operation failure.</summary>
    private string? _globalErrorMessage;

    /// <summary>Page-level successful-operation feedback.</summary>
    private string? _globalSuccessMessage;

    /// <summary>Bounded inventory projection.</summary>
    private ProjectInventoryPage? _inventory;

    /// <summary>Exposes an in-page retry only when inventory loading failed.</summary>
    private bool _inventoryReadFailed;

    /// <summary>Renderer-owned admission for coalesced inventory reads.</summary>
    private bool _inventoryRefreshInProgress;

    /// <summary>Requests one fresh inventory pass after notifications overlap an awaited read.</summary>
    private bool _inventoryRefreshPending;

    /// <summary>Fences superseded inventory replies.</summary>
    private int _inventoryVersion;

    /// <summary>Whether Azure credentials are available for this owner.</summary>
    private bool _isAzureConnected;

    /// <summary>Whether initial owned data is being loaded.</summary>
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

    /// <summary>Saved remote project selected for deployment.</summary>
    private Domain.Entities.Projects.Application? _selectedCloudApp;

    /// <summary>Local project directory supplied to variable discovery.</summary>
    private string? _selectedProjectPath;

    /// <summary>Connection dialog state.</summary>
    private bool _showAzureConnect;

    /// <summary>Whether deployment settings are visible.</summary>
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


    /// <summary>Current circuit authentication state.</summary>
    [Inject]
    private AuthenticationStateProvider AuthStateProvider { get; set; } = null!;


    /// <summary>Admits bounded deployment jobs.</summary>
    [Inject]
    private IDeploymentJobQueue DeploymentJobQueue { get; set; } = null!;


    /// <summary>Prepares cloud connections and admits requests without exposing provider credentials.</summary>
    [Inject]
    private ConnectedCloudDeploymentAdmission CloudAdmission { get; set; } = null!;


    /// <summary>Discovers deployment metadata and configuration variables.</summary>
    [Inject]
    private IProjectScannerService ProjectScanner { get; set; } = null!;

    /// <summary>Server-enforced hosting-profile capability flags.</summary>
    [Inject]
    private IDeploymentCapabilities DeploymentCapabilities { get; set; } = null!;

    /// <summary>Application navigation and absolute-link generation.</summary>
    [Inject]
    private NavigationManager NavigationManager { get; set; } = null!;

    /// <summary>Subscribes to project deployment status changes.</summary>
    [Inject]
    private IDeploymentStatusNotifier DeploymentStatusNotifier { get; set; } = null!;

    /// <summary>Browser interop for owned UI interactions.</summary>
    [Inject]
    private IJSRuntime JSRuntime { get; set; } = null!;

    /// <summary>Connection feedback supplied through the dashboard query parameter.</summary>
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
    ///     On initialization, we check if the user is authenticated. If they are, we attempt to retrieve
    ///     their projects using their user ID. If the user ID is not a valid GUID (which may be the case
    ///     for GitHub users), we look up the user in the database using their GitHub account ID and then
    ///     retrieve their projects using the internal user ID. If the user is not authenticated, we
    ///     simply set the loading state to false, which will trigger the UI to show the appropriate
    ///     message for unauthenticated users.
    /// </summary>
    /// <returns>A task representing completion of this operation, including any awaited callbacks.</returns>
    protected override async Task OnInitializedAsync()
    {
        AuthStateProvider.AuthenticationStateChanged += OnAuthenticationChanged;
        DeploymentStatusNotifier.OnStatusChanged += OnDeploymentStatusChanged;
        DeploymentJobQueue.StateChanged += OnQueueStateChanged;

        _currentUserId = await GetCurrentUserIdAsync();

        if (_currentUserId != Guid.Empty)
        {
            await using var connectionScope = ServiceScopes.CreateAsyncScope();
            _isAzureConnected = await connectionScope.ServiceProvider.GetRequiredService<IUserService>()
                .HasAzureConnectionAsync(_currentUserId);
            await RefreshAppsAsync();
        }

        if (!string.IsNullOrWhiteSpace(AzureConnectionError))
            _globalErrorMessage = $"Azure connection failed: {AzureConnectionError}";

        _isLoading = false;
    }


    /// <summary>
    ///     Event handler that is called whenever the deployment status of an app changes. This method
    ///     updates the status of the latest deployment for the affected app in the UI. If no deployment
    ///     record exists yet for the app, it triggers a refresh of the apps list to ensure the UI
    ///     reflects the status.
    /// </summary>
    /// <param name="appId">The unique identifier of the app whose deployment status has changed.</param>
    /// <param name="status">The new deployment status to be applied.</param>
    private void OnDeploymentStatusChanged(Guid appId, DeploymentStatus status)
    {
        _ = DispatchStatusChangedAsync(appId, status);
    }

    /// <summary>
    ///     Marshals model changes and rendering together onto the circuit and observes notification
    ///     failures.
    /// </summary>
    /// <param name="appId">Project identity associated with the notification.</param>
    /// <param name="status">New deployment status.</param>
    /// <returns>A task representing completion of this operation, including any awaited callbacks.</returns>
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

    /// <summary>
    ///     Updates the current model and deploy controls only on the renderer dispatcher.
    /// </summary>
    /// <param name="appId">Project identity associated with the notification.</param>
    /// <param name="status">New deployment status.</param>
    /// <returns>A task representing completion of this operation, including any awaited callbacks.</returns>
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
    /// <returns>A task representing completion of this operation, including any awaited callbacks.</returns>
    private async Task RefreshAppsAsync()
    {
        _inventoryRefreshPending = true;
        if (_inventoryRefreshInProgress || _disposed) return;
        _inventoryRefreshInProgress = true;
        try
        {
            while (_inventoryRefreshPending && !_disposed)
            {
                _inventoryRefreshPending = false;
                await RefreshAppsCoreAsync();
            }
        }
        finally
        {
            _inventoryRefreshInProgress = false;
        }
    }

    /// <summary>Reads one detached inventory in its own scope and fences stale replies.</summary>
    private async Task RefreshAppsCoreAsync()
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
            if (!_disposed && version == _inventoryVersion && !_inventoryRefreshPending)
            {
                _inventory = result;
                if (_inventoryReadFailed) _globalErrorMessage = null;
                _inventoryReadFailed = false;
            }
        }
        catch (OperationCanceledException) when (_disposed)
        {
        }
        catch (Exception)
        {
            if (!_disposed && version == _inventoryVersion)
            {
                _globalErrorMessage = "Projects could not be loaded. Refresh to try again.";
                _inventoryReadFailed = true;
            }
        }
        finally
        {
            if (!_disposed && version == _inventoryVersion) _isLoading = false;
        }
    }


    /// <summary>
    ///     Deletes an app by its ID. This method first checks if the current user ID is valid. If it is,
    ///     it calls the app service to delete the project.
    /// </summary>
    /// <param name="appId"></param>
    /// <returns>A task representing completion of this operation, including any awaited callbacks.</returns>
    private async Task DeleteAppAsync(Guid appId)
    {
        if (_currentUserId == Guid.Empty) return;

        ClearMessages();

        await using var scope = ServiceScopes.CreateAsyncScope();
        var success = await scope.ServiceProvider.GetRequiredService<IApplicationService>()
            .DeleteAppAsync(appId, _currentUserId);

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
    ///     Initiates the deployment process for a given app. If the app is local, it looks for a web
    ///     project within the solution. If a web project is found, it analyzes the app's dependencies to
    ///     prepare the deployment configuration.
    /// </summary>
    /// <param name="app"></param>
    /// <returns>A task representing completion of this operation, including any awaited callbacks.</returns>
    private async Task DeployAppAsync(Domain.Entities.Projects.Application app)
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
    ///     Cancels the deployment process by hiding the configuration modal and clearing any selected
    ///     project path or current deployment configuration.
    /// </summary>
    private void HideConfigModal()
    {
        _showConfigModal = false;
        _currentDeployConfig = null;
        _selectedProjectPath = null;
        _selectedCloudApp = null;
    }


    /// <summary>
    ///     Initiates the deployment process for a specified project configuration. This method handles
    ///     the deployment by invoking the local deployment orchestrator, updating the UI state, and
    ///     managing deployment success or failure messages.
    /// </summary>
    /// <param name="finalConfig">
    ///     The deployment configuration containing details such as project ID, project name, environment
    ///     settings, and database configuration.
    /// </param>
    /// <returns>A task representing the asynchronous deployment operation.</returns>
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

            var preparation = await CloudAdmission.PrepareAsync(owner, AuthStateProvider,
                true, () => !_disposed && owner == _currentUserId);
            if (preparation.State == CloudConnectionState.Obsolete) return;
            if (preparation.State == CloudConnectionState.MissingAzure)
            {
                _globalErrorMessage = "Connect your Azure account before deploying GitHub projects to the cloud.";
                return;
            }

            if (preparation.State == CloudConnectionState.MissingGitHub)
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
                    await preparation.StartAsync(finalConfig, repository.Owner, repository.Name,
                        cloudApp.Name, _pendingCloudIdempotencyKey);
                    _pendingCloudIdempotencyKey = null;
                }
                else
                {
                    await preparation.StartAsync(finalConfig, repository.Owner, repository.Name, cloudApp.Name, null);
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
            NavigationManager.NavigateTo($"/project/{finalConfig.ProjectId}");
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
        NavigationManager.NavigateTo($"/project/{finalConfig.ProjectId}");
    }


    /// <summary>
    ///     Reads bounded action metadata, retaining the legacy port for older host composition.
    /// </summary>
    /// <param name="services">The current operation scope.</param>
    /// <param name="project">The selected application identity.</param>
    /// <param name="owner">The captured initiating owner.</param>
    /// <param name="token">The existing page-lifetime token.</param>
    /// <returns>The owned current metadata, or null for an inaccessible project.</returns>
    private static Task<Domain.Entities.Projects.Application?> ReadActionProjectAsync(IServiceProvider services,
        Guid project,
        Guid owner, CancellationToken token)
    {
        return services.GetService<IProjectActionQuery>() is { } query
            ? query.GetAsync(project, owner, token)
            : services.GetRequiredService<IApplicationService>().GetAppByIdAsync(project, owner, token);
    }

    /// <summary>
    ///     Resolves the current user's internal AutoMate ID from authentication claims.
    /// </summary>
    /// <returns>A task returning the internal owner identity, or Guid.Empty when unauthenticated or unresolved.</returns>
    private async Task<Guid> GetCurrentUserIdAsync()
    {
        await using var scope = ServiceScopes.CreateAsyncScope();
        return await AuthenticatedUserResolver.GetCurrentUserIdAsync(AuthStateProvider,
            scope.ServiceProvider.GetRequiredService<IUserService>());
    }


    /// <summary>
    ///     Checks if a project is currently being deployed.
    /// </summary>
    /// <param name="projectId">The project identity used for authorization or correlation.</param>
    /// <returns>True when the project is preparing or has queued/active deployment work.</returns>
    private bool IsDeploying(Guid projectId)
    {
        var state = DeploymentJobQueue.GetProjectState(projectId);
        return _preparingProject == projectId || _deployingStates.GetValueOrDefault(projectId, false) ||
               state.QueuedDeployments > 0 || state.ActiveDeployments > 0;
    }

    /// <summary>
    ///     Checks stop preparation and background queue activity.
    /// </summary>
    /// <param name="projectId">The project identity used for authorization or correlation.</param>
    /// <returns>True when the project is preparing a stop or has queued/active stop work.</returns>
    private bool IsStopping(Guid projectId)
    {
        var state = DeploymentJobQueue.GetProjectState(projectId);
        return _preparingStops.Contains(projectId) || state.QueuedStops > 0 || state.ActiveStops > 0;
    }

    /// <summary>
    ///     Schedules a renderer update when a project queue changes.
    /// </summary>
    /// <param name="projectId">The project identity used for authorization or correlation.</param>
    private void OnQueueStateChanged(Guid projectId)
    {
        _ = DispatchQueueChangedAsync(projectId);
    }

    /// <summary>Reads presentation state only on the renderer and observes navigation races.</summary>
    private async Task DispatchQueueChangedAsync(Guid projectId)
    {
        if (_disposed) return;
        try
        {
            await InvokeAsync(() =>
            {
                if (!_disposed && (_inventory?.Items.Any(app => app.Id == projectId) == true ||
                                   _apps?.Any(app => app.Id == projectId) == true)) StateHasChanged();
            });
        }
        catch (Exception) when (_disposed)
        {
            // Navigation can retire the renderer after admission.
        }
        catch (Exception error)
        {
            Logger.LogWarning("Queue notification failed for {ProjectId}: {FailureType}.", projectId,
                error.GetType().Name);
        }
    }


    /// <summary>
    ///     Determines whether a project card's deploy action should be disabled.
    /// </summary>
    /// <param name="app">Saved project metadata used for this deployment.</param>
    /// <returns>True during admission, for disabled local deployments, or when a remote project lacks an Azure connection.</returns>
    private bool IsDeployDisabled(Domain.Entities.Projects.Application app)
    {
        return IsDeploying(app.Id) ||
               (app.SourceType == SourceType.Local && !DeploymentCapabilities.LocalDeploymentsEnabled) ||
               (app.SourceType == SourceType.Remote && !_isAzureConnected);
    }


    /// <summary>
    ///     Gets a short tooltip explaining why a remote deploy action is disabled.
    /// </summary>
    /// <param name="app">Saved project metadata used for this deployment.</param>
    /// <returns>Fixed Azure-connection guidance for a disconnected remote project; otherwise the deployment label.</returns>
    private string GetDeployButtonTitle(Domain.Entities.Projects.Application app)
    {
        return app.SourceType == SourceType.Remote && !_isAzureConnected
            ? "Connect to Azure to deploy GitHub projects."
            : "Deploy project";
    }


    /// <summary>
    ///     Creates a cloud deployment configuration for a saved remote repository.
    /// </summary>
    /// <param name="app">Saved project metadata used for this deployment.</param>
    /// <returns>Default deployment settings for the saved remote repository.</returns>
    private static DeploymentConfigDto CreateCloudDeploymentConfig(Domain.Entities.Projects.Application app)
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
    /// <param name="app">Saved project metadata used for this deployment.</param>
    /// <returns>The newest recorded deployment status, or null when no deployment exists.</returns>
    private static DeploymentStatus? GetLatestStatus(Domain.Entities.Projects.Application app)
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
    ///     Clears any global error or success messages. This is typically called before starting a new
    ///     operation to ensure that old messages do not persist and confuse the user.
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

    /// <summary>
    ///     Applies navigation query filters and refreshes the owned project inventory.
    /// </summary>
    /// <returns>A task representing completion of this operation, including any awaited callbacks.</returns>
    protected override async Task OnParametersSetAsync()
    {
        _search = Search ?? "";
        _source = Source ?? "";
        _status = Status ?? "";
        _sort = Sort is "name" or "saved" ? Sort : "activity";
        await RefreshAppsAsync();
    }

    /// <summary>
    ///     Encodes a stable filter URL for pagination and browser history.
    /// </summary>
    /// <param name="page">One-based numeric table page.</param>
    /// <returns>The dashboard URL retaining escaped filters and a page number of at least one.</returns>
    private string InventoryUrl(int page)
    {
        return
            $"/dashboard?q={Uri.EscapeDataString(_search)}&source={Uri.EscapeDataString(_source)}&status={Uri.EscapeDataString(_status)}&sort={_sort}&page={Math.Max(1, page)}";
    }

    /// <summary>
    ///     Applies filters through navigation and resets to the first page.
    /// </summary>
    private void ApplyFilters()
    {
        NavigationManager.NavigateTo(InventoryUrl(1));
    }

    /// <summary>
    ///     Loads only the selected owned entity graph when an action needs it.
    /// </summary>
    /// <param name="id">Selected project identity.</param>
    /// <returns>A task representing completion of this operation, including any awaited callbacks.</returns>
    private async Task DeployInventoryAsync(Guid id)
    {
        if (_disposed || _preparingProject is not null || IsStopping(id)) return;
        _preparingProject = id;
        var owner = _currentUserId;
        try
        {
            using var scope = ServiceScopes.CreateScope();
            var app = await ReadActionProjectAsync(scope.ServiceProvider, id, owner, _lifetime.Token);
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

    /// <summary>
    ///     Queues the existing local stop operation after reloading the owned running project.
    /// </summary>
    /// <param name="id">Selected project identity.</param>
    /// <returns>A task representing completion of this operation, including any awaited callbacks.</returns>
    private async Task StopInventoryAsync(Guid id)
    {
        if (_disposed || _currentUserId == Guid.Empty || IsDeploying(id) || IsStopping(id)) return;
        _preparingStops.Add(id);
        var owner = _currentUserId;
        _globalErrorMessage = null;
        _globalSuccessMessage = null;
        try
        {
            using var scope = ServiceScopes.CreateScope();
            var app = await ReadActionProjectAsync(scope.ServiceProvider, id, owner, _lifetime.Token);
            if (_disposed || owner != _currentUserId) return;
            if (app is null || app.UserId != owner)
            {
                _globalErrorMessage = "This project is no longer available.";
                return;
            }

            if (app.SourceType != SourceType.Local)
            {
                _globalErrorMessage = "Stop cloud deployments in your cloud provider's portal.";
                return;
            }

            if (!DeploymentCapabilities.LocalDeploymentsEnabled)
            {
                _globalErrorMessage = "Local deployments are disabled on this host.";
                return;
            }

            var latest = app.CsProjects.SelectMany(project => project.Deployments)
                .OrderByDescending(deployment => deployment.CreatedAt).FirstOrDefault();
            var webProject = app.CsProjects.FirstOrDefault(project => project.IsWebProject);
            if (latest?.Status != DeploymentStatus.Running || webProject is null || IsDeploying(id))
            {
                _globalErrorMessage = "This project has no running deployment to stop.";
                return;
            }

            await DeploymentJobQueue.EnqueueAsync(new StopLocalDeploymentJob(id, app.Name, webProject.Path),
                _lifetime.Token);
            if (!_disposed && owner == _currentUserId) _globalSuccessMessage = "Deployment stop queued.";
        }
        catch (OperationCanceledException) when (_disposed)
        {
        }
        catch (Exception exception)
        {
            Logger.LogError(exception, "Could not queue project stop for {ProjectId}", id);
            if (!_disposed) _globalErrorMessage = "The deployment could not be stopped. Try again shortly.";
        }
        finally
        {
            _preparingStops.Remove(id);
        }
    }

    /// <summary>
    ///     Opens the project removal confirmation.
    /// </summary>
    /// <param name="id">Selected project identity.</param>
    private void ConfirmRemoval(Guid id)
    {
        _removeProject = id;
    }

    /// <summary>
    ///     Dismisses project removal.
    /// </summary>
    private void CancelRemoval()
    {
        _removeProject = null;
    }

    /// <summary>
    ///     Removes the explicitly selected owned project and reloads its page.
    /// </summary>
    /// <param name="id">Selected project identity.</param>
    /// <returns>A task representing completion of this operation, including any awaited callbacks.</returns>
    private async Task RemoveConfirmedAsync(Guid id)
    {
        _removeProject = null;
        await DeleteAppAsync(id);
    }

    /// <summary>
    ///     Dismisses the connection dialog.
    /// </summary>
    private void CloseAzureDialog()
    {
        _showAzureConnect = false;
    }

    /// <summary>
    ///     Clears project state when authentication changes on a still-open circuit.
    /// </summary>
    /// <param name="state">Pending authentication state used to detect an owner change.</param>
    private void OnAuthenticationChanged(Task<AuthenticationState> state)
    {
        _ = RebindOwnerAsync();
    }

    /// <summary>
    ///     Fences old-owner replies and closes configuration before binding the new account.
    /// </summary>
    /// <returns>A task representing completion of this operation, including any awaited callbacks.</returns>
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
