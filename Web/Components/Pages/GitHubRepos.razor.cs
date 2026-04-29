using System.Security.Claims;
using Core.DTO;
using Core.Entities;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.EntityFrameworkCore;
using Services.Data;
using Services.GitHub;
using Services.Projects;

namespace Web.Components.Pages;

/// <summary>
///     The GitHubRepos component is responsible for retrieving and displaying a list of GitHub repositories
///     associated with the authenticated user. It checks the user's authentication state and integrates
///     with the database to determine if the user is a GitHub user. If a valid GitHub access token is available,
///     the component leverages the GitHub service to fetch the repositories.
/// </summary>
public partial class GitHubRepos : ComponentBase
{
    private List<GitHubRepositoryDto>? _githubRepos;

    private bool _isErrorStatus;
    private bool _isGitHubUser;
    private bool _isLoading = true;
    private string? _statusMessage;

    [Inject] private AuthenticationStateProvider AuthStateProvider { get; set; } = null!;

    [Inject] private IServiceProvider ServiceProvider { get; set; } = null!;

    [Inject] private IGitHubService GitHubService { get; set; } = null!;

    [Inject] private IProjectService ProjectService { get; set; } = null!;


    /// <summary>
    ///     On component initialization, we check the user's authentication state.
    ///     If authenticated, we attempt to find the user in our database. If the user
    ///     is a GitHub user with a valid access token, we fetch their repositories from GitHub.
    ///     We also handle loading states and potential issues with fetching repositories.
    /// </summary>
    protected override async Task OnInitializedAsync()
    {
        var dbUser = await GetCurrentUserFromDbAsync();

        if (dbUser is GitHubUser ghUser && !string.IsNullOrEmpty(ghUser.AccessToken))
        {
            _isGitHubUser = true;
            _githubRepos = await GitHubService.GetUserRepositoriesAsync(ghUser.AccessToken);
        }

        _isLoading = false;
    }


    /// <summary>
    ///     When the user clicks the "Save" button on a repository card, this method is called.
    ///     It retrieves the current user's ID and attempts to save the selected GitHub repository
    ///     as a project in our system. The method also handles success and error states, providing
    ///     feedback to the user through status messages.
    /// </summary>
    /// <param name="repo">The DTO representing the GitHub repository to be saved.</param>
    private async Task SaveProjectAsync(GitHubRepositoryDto repo)
    {
        var user = await GetCurrentUserFromDbAsync();

        if (user == null || user.Id == Guid.Empty)
        {
            SetStatusMessage("Authentication error. Please log in again.", true);
            return;
        }

        var success = await ProjectService.AddGitHubProjectAsync(user.Id, repo.Name, repo.HtmlUrl);

        if (success)
            SetStatusMessage($"{repo.Name} successfully saved!", false);
        else
            SetStatusMessage($"{repo.Name} repository is already saved!", true);
    }


    /// <summary>
    ///     Extracts user authentication checks and database fetching into a single, reusable method.
    ///     Safely handles the DbContext connection for Blazor Server using a Scope.
    /// </summary>
    private async Task<User?> GetCurrentUserFromDbAsync()
    {
        var authState = await AuthStateProvider.GetAuthenticationStateAsync();
        var claimsPrincipal = authState.User;

        if (!claimsPrincipal.Identity?.IsAuthenticated ?? true)
            return null;

        var nameIdentifier = claimsPrincipal.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        if (string.IsNullOrEmpty(nameIdentifier))
            return null;

        using var scope = ServiceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AutoMateDbContext>();

        // First try to parse as LocalUser (GUID)
        if (Guid.TryParse(nameIdentifier, out var localUserId))
            return await db.Users.FindAsync(localUserId);

        // Fallback: GitHubUser uses string-based AccountId
        return await db.Users.OfType<GitHubUser>().FirstOrDefaultAsync(u => u.AccountId == nameIdentifier);
    }


    /// <summary>
    ///     Sets the status message to be displayed to the user, along with an indication
    ///     of whether it's an error message or not.
    /// </summary>
    /// <param name="message">The message to be displayed.</param>
    /// <param name="isError">An indication of whether the message is an error.</param>
    private void SetStatusMessage(string message, bool isError)
    {
        _statusMessage = message;
        _isErrorStatus = isError;
    }


    /// <summary>
    ///     Clears the current status message and resets the error status flag.
    /// </summary>
    private void ClearStatusMessage()
    {
        _statusMessage = null;
        _isErrorStatus = false;
    }
}