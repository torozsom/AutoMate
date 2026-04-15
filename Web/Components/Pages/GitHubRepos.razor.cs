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

    [Inject] private AutoMateDbContext DbContext { get; set; } = null!;

    [Inject] private IGitHubService GitHubService { get; set; } = null!;

    [Inject] private IProjectService ProjectService { get; set; } = null!;

    [Inject] private IServiceProvider ServiceProvider { get; set; } = null!;


    /// <summary>
    ///     On component initialization, we check the user's authentication state.
    ///     If authenticated, we attempt to find the user in our database. If the user
    ///     is a GitHub user with a valid access token, we fetch their repositories from GitHub.
    ///     We also handle loading states and potential issues with fetching repositories.
    /// </summary>
    protected override async Task OnInitializedAsync()
    {
        var authState = await AuthStateProvider.GetAuthenticationStateAsync();
        var user = authState.User;

        if (user.Identity is { IsAuthenticated: true })
        {
            var nameIdentifier = user.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            User? dbUser = null;

            if (Guid.TryParse(nameIdentifier, out var localUserId))
                dbUser = await DbContext.Users.FindAsync(localUserId);

            else if (!string.IsNullOrEmpty(nameIdentifier))
                dbUser = await DbContext.Users.OfType<GitHubUser>()
                    .FirstOrDefaultAsync(u => u.AccountId == nameIdentifier);

            if (dbUser is GitHubUser ghUser && !string.IsNullOrEmpty(ghUser.AccessToken))
            {
                _isGitHubUser = true;
                _githubRepos = await GitHubService.GetUserRepositoriesAsync(ghUser.AccessToken);
            }
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
        var authState = await AuthStateProvider.GetAuthenticationStateAsync();
        var user = authState.User;

        if (!user.Identity?.IsAuthenticated ?? true)
            return;

        var userIdString = user.FindFirst(ClaimTypes.NameIdentifier)?.Value;

        if (!Guid.TryParse(userIdString, out var userId))
        {
            using var scope = ServiceProvider.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AutoMateDbContext>();
            var dbUser = await db.Users.OfType<GitHubUser>().FirstOrDefaultAsync(u => u.AccountId == userIdString);

            if (dbUser != null)
                userId = dbUser.Id;
        }

        if (userId == Guid.Empty)
            return;

        var success = await ProjectService.AddGitHubProjectAsync(userId, repo.Name, repo.HtmlUrl);

        if (success)
        {
            _statusMessage = $"{repo.Name} successfully saved!";
            _isErrorStatus = false;
        }
        else
        {
            _statusMessage = $"{repo.Name} repository is already saved!";
            _isErrorStatus = true;
        }
    }
}