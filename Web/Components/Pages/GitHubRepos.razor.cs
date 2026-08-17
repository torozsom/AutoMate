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
public partial class GitHubRepos : ComponentBase, IDisposable
{
    /// A cancellation token source to manage asynchronous operations and ensure they are cancelled when the component is
    /// disposed.
    private readonly CancellationTokenSource _componentCancellation = new();

    /// A set to keep track of the URLs of repositories that are currently being saved.
    private readonly HashSet<string> _savingRepositoryUrls = new(StringComparer.OrdinalIgnoreCase);

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

    /// Logger for logging errors and important information within the component.
    [Inject]
    private ILogger<GitHubRepos> Logger { get; set; } = null!;


    public void Dispose()
    {
        _componentCancellation.Cancel();
        _componentCancellation.Dispose();
        GC.SuppressFinalize(this);
    }


    /// <summary>
    ///     On component initialization, we check the user's authentication state.
    ///     If authenticated, we attempt to find the user in our database. If the user
    ///     is a GitHub user with a valid access token, we fetch their repositories from GitHub.
    ///     We also handle loading states and potential issues with fetching repositories.
    /// </summary>
    protected override async Task OnInitializedAsync()
    {
        try
        {
            var userDetails = await AuthenticatedUserResolver.GetCurrentUserDetailsAsync(
                AuthStateProvider,
                UserService,
                _componentCancellation.Token);

            _currentUserId = userDetails.UserId;
            _isGitHubUser = userDetails.IsGitHubUser;

            if (_isGitHubUser && !string.IsNullOrEmpty(userDetails.AccessToken))
                _githubRepos = await GitHubService.GetUserRepositoriesAsync(userDetails.AccessToken,
                    cancellationToken: _componentCancellation.Token);
        }
        catch (OperationCanceledException)
        {
            // Component was disposed while the repository list was loading.
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to load GitHub repositories.");
            SetStatusMessage("Failed to load GitHub repositories. Please try again later.", true);
        }
        finally
        {
            _isLoading = false;
        }
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

        if (!_savingRepositoryUrls.Add(repo.HtmlUrl))
            return;

        try
        {
            var success = await ApplicationService.AddGitHubAppAsync(_currentUserId, repo.Name, repo.HtmlUrl,
                _componentCancellation.Token);

            if (success)
                SetStatusMessage($"{repo.Name} successfully saved!", false);
            else
                SetStatusMessage($"{repo.Name} repository is already saved!", true);
        }
        catch (OperationCanceledException)
        {
            // Component was disposed while saving.
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to save GitHub repository {RepositoryUrl}.", repo.HtmlUrl);
            SetStatusMessage("Failed to save the repository. Please try again later.", true);
        }
        finally
        {
            _savingRepositoryUrls.Remove(repo.HtmlUrl);
        }
    }


    /// <summary>
    ///     Indicates whether the repository is currently being saved.
    /// </summary>
    /// <param name="repo">The repository to inspect.</param>
    /// <returns><see langword="true" /> when a save operation is already active for the repository.</returns>
    private bool IsSaving(GitHubRepositoryDto repo)
    {
        return _savingRepositoryUrls.Contains(repo.HtmlUrl);
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