using System.Security.Claims;
using Core.Entities;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.EntityFrameworkCore;
using Services.Data;
using Services.Projects;

namespace Web.Components.Pages;


/// <summary>
/// Represents a dashboard component that displays user-specific project data.
/// This component is responsible for verifying user authentication and fetching
/// the associated projects based on the authenticated user's ID.
/// </summary>
public partial class Dashboard : ComponentBase
{
    [Inject]
    private AuthenticationStateProvider AuthStateProvider { get; set; } = null!;

    [Inject]
    private IProjectService ProjectService { get; set; } = null!;

    [Inject]
    private IServiceProvider ServiceProvider { get; set; } = null!;

    private bool _isLoading = true;

    private List<Project>? _projects;

    private Guid _currentUserId;


    /// <summary>
    ///     On initialization, we check if the user is authenticated. If they are,
    ///     we attempt to retrieve their projects using their user ID. If the user ID
    ///     is not a valid GUID (which may be the case for GitHub users), we look up the
    ///     user in the database using their GitHub account ID and then retrieve their projects
    ///     using the internal user ID.
    ///
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
                _currentUserId = userId;

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
}
