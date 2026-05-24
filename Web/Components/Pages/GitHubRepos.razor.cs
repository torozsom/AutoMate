using System.Security.Claims;
using Core.DTO;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Services.Data.Apps;
using Services.Data.Users;
using Services.GitHub;

namespace Web.Components.Pages;

/// <summary>
///     The GitHubRepos component is responsible for retrieving and displaying a list of GitHub repositories
///     associated with the authenticated user. It checks the user's authentication state and integrates
///     with the user service to fetch GitHub tokens securely.
/// </summary>
public partial class GitHubRepos : ComponentBase
{
    /// The current user's identifier.
    private Guid _currentUserId;

    /// The list of GitHub repositories associated with the user.
    private List<GitHubRepositoryDto>? _githubRepos;

    /// A flag to indicate if the current status message is an error message.
    private bool _isErrorStatus;

    /// A flag indicating whether the user is a GitHub user.
    private bool _isGitHubUser;

    /// A flag indicating whether the component is currently loading data.
    private bool _isLoading = true;

    /// The current status message to be displayed to the user.
    private string? _statusMessage;


    /// Authentication State Provider for checking user authentication and retrieving user information.
    [Inject]
    private AuthenticationStateProvider AuthStateProvider { get; set; } = null!;

    /// Service for managing user accounts.
    [Inject]
    private IUserService UserService { get; set; } = null!;

    /// Service for interacting with the GitHub API.
    [Inject]
    private IGitHubService GitHubService { get; set; } = null!;

    /// Service for managing projects, including fetching, creating, and deleting projects associated with users.
    [Inject]
    private IApplicationService ApplicationService { get; set; } = null!;


    /// <summary>
    ///     On component initialization, we check the user's authentication state.
    ///     If authenticated, we attempt to find the user in our database. If the user
    ///     is a GitHub user with a valid access token, we fetch their repositories from GitHub.
    ///     We also handle loading states and potential issues with fetching repositories.
    /// </summary>
    protected override async Task OnInitializedAsync()
    {
        var authState = await AuthStateProvider.GetAuthenticationStateAsync();
        var userClaims = authState.User;

        if (userClaims.Identity is not null && userClaims.Identity.IsAuthenticated)
        {
            var nameIdentifier = userClaims.FindFirst(ClaimTypes.NameIdentifier)?.Value;

            if (!string.IsNullOrEmpty(nameIdentifier))
            {
                var (userId, accessToken, isGitHubUser) =
                    await UserService.GetUserDetailsFromIdentifierAsync(nameIdentifier);

                _currentUserId = userId;
                _isGitHubUser = isGitHubUser;

                if (_isGitHubUser && !string.IsNullOrEmpty(accessToken))
                    _githubRepos = await GitHubService.GetUserRepositoriesAsync(accessToken);
            }
        }

        _isLoading = false;
    }


    /// <summary>
    ///     When the user clicks the "Save" button on a repository card, this method is called.
    ///     It attempts to save the selected GitHub repository as a project in our system.
    /// </summary>
    /// <param name="repo">The DTO representing the GitHub repository to be saved.</param>
    private async Task SaveProjectAsync(GitHubRepositoryDto repo)
    {
        ClearStatusMessage();

        if (_currentUserId == Guid.Empty)
        {
            SetStatusMessage("Authentication error. Please log in again.", true);
            return;
        }

        var success = await ApplicationService.AddGitHubAppAsync(_currentUserId, repo.Name, repo.HtmlUrl);

        if (success)
            SetStatusMessage($"{repo.Name} successfully saved!", false);
        else
            SetStatusMessage($"{repo.Name} repository is already saved!", true);
    }


    /// <summary>
    ///     Sets the status message to be displayed to the user.
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