using System.Collections.Concurrent;
using Core.DTO;
using Core.Entities;
using Core.Enums;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.JSInterop;
using Services.Data.Apps;
using Services.Data.Users;
using Services.Orchestration;
using Services.Scanner;
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

    /// The list of apps associated with the authenticated user, fetched from the database.
    private List<Application>? _apps;

    /// The Azure tenant ID entered for personal Microsoft account connections.
    private string _azureTenantId = string.Empty;

    /// The current deployment configuration being edited by the user, if any.
    private DeploymentConfigDto? _currentDeployConfig;

    /// The ID of the currently authenticated user, used to fetch and manage their projects.
    private Guid _currentUserId;

    /// A message to display global errors that occur during operations like deployment or project fetching.
    private string? _globalErrorMessage;

    /// A message to display global success notifications, such as successful deployments.
    private string? _globalSuccessMessage;

    /// A flag indicating whether the current user has connected an Azure account.
    private bool _isAzureConnected;

    /// A flag indicating whether the component is currently loading data, used to show loading indicators in the UI.
    private bool _isLoading = true;

    /// The remote application currently selected for cloud deployment.
    private Application? _selectedCloudApp;

    /// The file system path of the project currently selected for deployment configuration.
    private string? _selectedProjectPath;

    /// A flag indicating whether the deployment configuration modal is currently visible to the user.
    private bool _showConfigModal;


    /// Authentication State Provider for checking user authentication and retrieving user information.
    [Inject]
    private AuthenticationStateProvider AuthStateProvider { get; set; } = null!;

    /// Service for managing projects, including fetching, creating, and deleting projects associated with users.
    [Inject]
    private IApplicationService ApplicationService { get; set; } = null!;

    /// Queue that hands deployment work to the hosted background worker.
    [Inject]
    private IDeploymentJobQueue DeploymentJobQueue { get; set; } = null!;

    /// Service for managing user accounts.
    [Inject]
    private IUserService UserService { get; set; } = null!;

    /// Service responsible for scanning project files to extract metadata and analyze dependencies.
    [Inject]
    private IProjectScannerService ProjectScanner { get; set; } = null!;

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


    /// <summary>
    ///     Disposes of the component by unsubscribing from the deployment status change notifications.
    /// </summary>
    public void Dispose()
    {
        DeploymentStatusNotifier.OnStatusChanged -= OnDeploymentStatusChanged;
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
        DeploymentStatusNotifier.OnStatusChanged += OnDeploymentStatusChanged;

        _currentUserId = await GetCurrentUserIdAsync();

        if (_currentUserId != Guid.Empty)
        {
            _isAzureConnected = await UserService.HasAzureConnectionAsync(_currentUserId);
            _apps = await ApplicationService.GetUserAppsAsync(_currentUserId);
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
        var app = _apps?.FirstOrDefault(p => p.Id == appId);
        if (app is null) return;

        var latestDeployment = app.CsProjects
            .SelectMany(c => c.Deployments)
            .MaxBy(d => d.CreatedAt);

        if (latestDeployment is not null)
            latestDeployment.Status = status;
        else
            _ = RefreshAppsAsync();

        if (status is DeploymentStatus.Running or DeploymentStatus.Failed or DeploymentStatus.Stopped)
            SetDeployingState(appId, false);

        InvokeAsync(StateHasChanged);
    }


    /// <summary>
    ///     Refreshes the list of apps for the current user.
    /// </summary>
    private async Task RefreshAppsAsync()
    {
        if (_currentUserId != Guid.Empty)
        {
            _apps = await ApplicationService.GetUserAppsAsync(_currentUserId);
            await InvokeAsync(StateHasChanged);
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

        if (success && _apps is not null)
            _apps.RemoveAll(p => p.Id == appId);
        else
            _globalErrorMessage = "Failed to remove the project. It might have been already deleted.";
    }


    /// <summary>
    ///     Initiates the deployment process for a given app. If the app is local,
    ///     it looks for a web project within the solution. If a web project is found, it analyzes
    ///     the app's dependencies to prepare the deployment configuration.
    /// </summary>
    /// <param name="app"></param>
    private async Task DeployAppAsync(Application app)
    {
        ClearMessages();

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
        catch (Exception ex)
        {
            _globalErrorMessage = $"Failed to analyze project dependencies: {ex.Message}";
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

            var azureCredentials = await UserService.GetAzureCloudCredentialsAsync(_currentUserId);
            if (azureCredentials == null)
            {
                _globalErrorMessage = "Connect your Azure account before deploying GitHub projects to the cloud.";
                return;
            }

            var userDetails = await GetCurrentUserDetailsAsync();
            if (string.IsNullOrWhiteSpace(userDetails.AccessToken))
            {
                _globalErrorMessage = "Connect your GitHub account before deploying GitHub projects to the cloud.";
                return;
            }

            SetDeployingState(finalConfig.ProjectId, true);

            try
            {
                await DeploymentJobQueue.EnqueueAsync(new CloudDeploymentJob(new CloudDeploymentRequestDto
                {
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

                _globalSuccessMessage =
                    $"Cloud deployment workflow for '{finalConfig.ProjectName}' has been queued.";
            }
            catch (Exception ex)
            {
                _globalErrorMessage = $"Failed to queue cloud deployment for '{finalConfig.ProjectName}': {ex.Message}";
                SetDeployingState(finalConfig.ProjectId, false);
                return;
            }

            await JSRuntime.InvokeVoidAsync("open", $"/project/{finalConfig.ProjectId}", "_blank");
            return;
        }

        SetDeployingState(finalConfig.ProjectId, true);

        try
        {
            await DeploymentJobQueue.EnqueueAsync(new LocalDeploymentJob(finalConfig));
            _globalSuccessMessage = $"The '{finalConfig.ProjectName}' deployment has been queued.";
        }
        catch (Exception ex)
        {
            _globalErrorMessage = $"Failed to queue deployment for '{finalConfig.ProjectName}': {ex.Message}";
            SetDeployingState(finalConfig.ProjectId, false);
            return;
        }

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
        return _deployingStates.GetValueOrDefault(projectId, false);
    }


    /// <summary>
    ///     Determines whether a project card's deploy action should be disabled.
    /// </summary>
    private bool IsDeployDisabled(Application app)
    {
        return IsDeploying(app.Id) || (app.SourceType == SourceType.Remote && !_isAzureConnected);
    }


    /// <summary>
    ///     Gets a short tooltip explaining why a remote deploy action is disabled.
    /// </summary>
    private string GetDeployButtonTitle(Application app)
    {
        return app.SourceType == SourceType.Remote && !_isAzureConnected
            ? "Connect to Azure to deploy GitHub projects."
            : "Deploy project";
    }


    /// <summary>
    ///     Creates a cloud deployment configuration for a saved remote repository.
    /// </summary>
    private static DeploymentConfigDto CreateCloudDeploymentConfig(Application app)
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
    private static DeploymentStatus? GetLatestStatus(Application app)
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
}