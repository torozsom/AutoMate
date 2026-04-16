using System.Security.Claims;
using Core.Entities;
using Core.Enums;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.EntityFrameworkCore;
using Services.Data;
using Services.Orchestration;
using Services.Projects;

namespace Web.Components.Pages;

/// <summary>
///     Represents a dashboard component that displays user-specific project data.
///     This component is responsible for verifying user authentication and fetching
///     the associated projects based on the authenticated user's ID.
/// </summary>
public partial class Dashboard : ComponentBase
{
    private readonly Dictionary<Guid, bool> _deployingStates = new();
    private Guid _currentUserId;
    private string? _globalErrorMessage;

    private string? _globalSuccessMessage;
    private bool _isLoading = true;
    private List<Project>? _projects;

    [Inject] private AuthenticationStateProvider AuthStateProvider { get; set; } = null!;
    [Inject] private IProjectService ProjectService { get; set; } = null!;
    [Inject] private IServiceProvider ServiceProvider { get; set; } = null!;
    [Inject] private ILocalDeploymentOrchestrator DeploymentOrchestrator { get; set; } = null!;


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
        var authState = await AuthStateProvider.GetAuthenticationStateAsync();
        var user = authState.User;

        if (user.Identity is { IsAuthenticated: true })
        {
            var userIdString = user.FindFirst(ClaimTypes.NameIdentifier)?.Value;

            if (Guid.TryParse(userIdString, out var userId))
            {
                _currentUserId = userId;
            }
            else if (!string.IsNullOrEmpty(userIdString))
            {
                using var scope = ServiceProvider.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<AutoMateDbContext>();
                var dbUser = await db.Users.OfType<GitHubUser>().FirstOrDefaultAsync(u => u.AccountId == userIdString);

                if (dbUser != null)
                    _currentUserId = dbUser.Id;
            }

            if (_currentUserId != Guid.Empty)
                _projects = await ProjectService.GetProjectsAsync(_currentUserId);
        }

        _isLoading = false;
    }


    /// <summary>
    ///     Deletes a project by its ID. This method first checks if the current user ID is valid.
    ///     If it is, it calls the project service to delete the project.
    /// </summary>
    /// <param name="projectId"></param>
    private async Task DeleteProject(Guid projectId)
    {
        if (_currentUserId == Guid.Empty)
            return;

        var success = await ProjectService
            .DeleteProjectAsync(projectId, _currentUserId);

        if (success && _projects != null)
            _projects.RemoveAll(p => p.Id == projectId);
    }


    /// <summary>
    ///     Deploys a project asynchronously. This method verifies if the project is a local
    ///     web project and initiates its deployment. It manages the deployment state,
    ///     displays appropriate success or error messages, and updates the UI accordingly.
    /// </summary>
    /// <param name="project">
    ///     The project to be deployed, containing details such as ID, name, source type, and associated C#
    ///     projects.
    /// </param>
    /// <return>Returns a <see cref="Task" /> that represents the asynchronous operation.</return>
    private async Task DeployProjectAsync(Project project)
    {
        if (project.SourceType == SourceType.Remote)
        {
            _globalErrorMessage = "This feature is not yet available for remote projects.";
            return;
        }

        var csProjectToDeploy = project.CsProjects.FirstOrDefault(csp => csp.IsWebProject);
        if (csProjectToDeploy == null)
        {
            _globalErrorMessage = $"No web project found in '{project.Name}' to deploy.";
            return;
        }

        try
        {
            _globalErrorMessage = null;
            _globalSuccessMessage = null;
            _deployingStates[project.Id] = true;
            StateHasChanged();

            var deployment = await DeploymentOrchestrator.DeployLocalProjectAsync(csProjectToDeploy.Id);
            _globalSuccessMessage =
                $"The '{project.Name}' project has been successfully deployed! Container ID: {deployment.DockerContainerId}";
        }
        catch (Exception ex)
        {
            _globalErrorMessage = $"Failed to deploy '{project.Name}': {ex.Message}";
        }
        finally
        {
            _deployingStates[project.Id] = false;
            StateHasChanged();
        }
    }


    // Helper method to check if a project is currently being deployed.
    private bool IsDeploying(Guid projectId)
    {
        return _deployingStates.GetValueOrDefault(projectId, false);
    }
}