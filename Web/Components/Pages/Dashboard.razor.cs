using System.Security.Claims;
using Core.DTO;
using Core.Entities;
using Core.Enums;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.EntityFrameworkCore;
using Services.Data;
using Services.Orchestration;
using Services.Projects;
using Services.Scanner;

namespace Web.Components.Pages;

/// <summary>
///     Represents a dashboard component that displays user-specific project data.
///     This component is responsible for verifying user authentication and fetching
///     the associated projects based on the authenticated user's ID.
/// </summary>
public partial class Dashboard : ComponentBase
{
    /// A dictionary containing the deployment states of projects.
    private readonly Dictionary<Guid, bool> _deployingStates = new();

    /// The current deployment configuration being edited by the user, if any.
    private DeploymentConfigDto? _currentDeployConfig;

    /// The ID of the currently authenticated user, used to fetch and manage their projects.
    private Guid _currentUserId;

    /// A message to display global errors that occur during operations like deployment or project fetching.
    private string? _globalErrorMessage;

    /// A message to display global success notifications, such as successful deployments.
    private string? _globalSuccessMessage;

    /// A flag indicating whether the component is currently loading data, used to show loading indicators in the UI.
    private bool _isLoading = true;

    /// The list of projects associated with the authenticated user, fetched from the database.
    private List<Project>? _projects;

    /// The file system path of the project currently selected for deployment configuration.
    private string? _selectedProjectPath;

    /// A flag indicating whether the deployment configuration modal is currently visible to the user.
    private bool _showConfigModal;

    /// Authentication State Provider for checking user authentication and retrieving user information.
    [Inject]
    private AuthenticationStateProvider AuthStateProvider { get; set; } = null!;

    /// Service for managing projects, including fetching, creating, and deleting projects associated with users.
    [Inject]
    private IProjectService ProjectService { get; set; } = null!;

    /// Service provider for creating scopes and resolving services, used for database access and other operations.
    [Inject]
    private IServiceProvider ServiceProvider { get; set; } = null!;

    /// Service responsible for orchestrating the deployment process of local projects.
    [Inject]
    private ILocalDeploymentOrchestrator DeploymentOrchestrator { get; set; } = null!;

    /// Service responsible for scanning project files to extract metadata and analyze dependencies.
    [Inject]
    private IProjectScannerService ProjectScanner { get; set; } = null!;


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
        _currentUserId = await GetCurrentUserIdAsync();

        if (_currentUserId != Guid.Empty) _projects = await ProjectService.GetProjectsAsync(_currentUserId);

        _isLoading = false;
    }


    /// <summary>
    ///     Deletes a project by its ID. This method first checks if the current user ID is valid.
    ///     If it is, it calls the project service to delete the project.
    /// </summary>
    /// <param name="projectId"></param>
    private async Task DeleteProjectAsync(Guid projectId)
    {
        if (_currentUserId == Guid.Empty) return;

        ClearMessages();

        var success = await ProjectService.DeleteProjectAsync(projectId, _currentUserId);

        if (success && _projects != null)
            _projects.RemoveAll(p => p.Id == projectId);
        else
            _globalErrorMessage = "Failed to remove the project. It might have been already deleted.";
    }


    /// <summary>
    ///     Initiates the deployment process for a given project. If the project is local,
    ///     it looks for a web project within the solution. If a web project is found, it analyzes
    ///     the project's dependencies to prepare the deployment configuration.
    /// </summary>
    /// <param name="project"></param>
    private async Task DeployProjectAsync(Project project)
    {
        ClearMessages();

        if (project.SourceType == SourceType.Remote)
        {
            _globalErrorMessage = "Cloud deployment for GitHub projects is not yet available in this version.";
            return;
        }

        var csProjectToDeploy = project.CsProjects.FirstOrDefault(csp => csp.IsWebProject);

        if (csProjectToDeploy == null)
        {
            _globalErrorMessage = $"No web project found in '{project.Name}' to deploy. Only web apps are supported.";
            return;
        }

        try
        {
            _currentDeployConfig = await ProjectScanner.AnalyzeDependenciesAsync(project, csProjectToDeploy);
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
    }


    /// <summary>
    ///     Executes the deployment process using the provided deployment configuration.
    /// </summary>
    /// <param name="finalConfig">The final configuration for the project's deployment.</param>
    private async Task ExecuteDeploymentAsync(DeploymentConfigDto finalConfig)
    {
        HideConfigModal(); // Hide modal immediately
        ClearMessages();

        try
        {
            SetDeployingState(finalConfig.ProjectId, true);

            await DeploymentOrchestrator.DeployLocalProjectAsync(finalConfig);

            _globalSuccessMessage = $"The '{finalConfig.ProjectName}' project has been successfully deployed!";
        }
        catch (Exception ex)
        {
            _globalErrorMessage = $"Failed to deploy '{finalConfig.ProjectName}': {ex.Message}";
        }
        finally
        {
            SetDeployingState(finalConfig.ProjectId, false);
        }
    }


    /// <summary>
    ///     Helper method to safely extract the current user's ID from claims or fallback to the database.
    /// </summary>
    private async Task<Guid> GetCurrentUserIdAsync()
    {
        var authState = await AuthStateProvider.GetAuthenticationStateAsync();
        var user = authState.User;

        if (!user.Identity?.IsAuthenticated ?? true)
            return Guid.Empty;

        var userIdString = user.FindFirst(ClaimTypes.NameIdentifier)?.Value;

        if (Guid.TryParse(userIdString, out var parsedId))
            return parsedId;

        // Fallback for GitHub users (AccountId is a string)
        if (!string.IsNullOrEmpty(userIdString))
        {
            using var scope = ServiceProvider.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AutoMateDbContext>();
            var dbUser = await db.Users.OfType<GitHubUser>().FirstOrDefaultAsync(u => u.AccountId == userIdString);

            return dbUser?.Id ?? Guid.Empty;
        }

        return Guid.Empty;
    }


    /// Helper method to check if a project is currently being deployed.
    private bool IsDeploying(Guid projectId)
    {
        return _deployingStates.GetValueOrDefault(projectId, false);
    }


    /// <summary>
    ///     Sets the deploying state for a specific project. This is used to track which projects are currently
    ///     in the process of being deployed, allowing the UI to show appropriate loading indicators and prevent
    ///     multiple deployments of the same project at the same time.
    /// </summary>
    /// <param name="projectId">The ID of the project.</param>
    /// <param name="isDeploying">Indicates whether the project is currently deploying.</param>
    private void SetDeployingState(Guid projectId, bool isDeploying)
    {
        _deployingStates[projectId] = isDeploying;
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
}