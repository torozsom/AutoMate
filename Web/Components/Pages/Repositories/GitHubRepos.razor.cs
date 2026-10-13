using Application.Abstractions.GitHub;
using Application.Data.Apps;
using Application.Data.Users;
using Domain.DTO;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Web.Services;

namespace Web.Components.Pages;

/// <summary>
///     The GitHubRepos component is responsible for retrieving and displaying a list of GitHub
///     repositories associated with the authenticated user. It checks the user's authentication state
///     and integrates with the user service to fetch GitHub tokens securely.
/// </summary>
public partial class GitHubRepos : ComponentBase, IDisposable
{
    /// <summary>Cancels operations when the component is disposed.</summary>
    private readonly CancellationTokenSource _componentCancellation = new();

    /// <summary>Repository URLs currently being saved, compared without case sensitivity.</summary>
    private readonly HashSet<string> _savingRepositoryUrls = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Resolved internal owner identity.</summary>
    private Guid _currentUserId;

    /// <summary>Fetched repositories, or null before loading.</summary>
    private List<GitHubRepositoryDto>? _githubRepos;

    /// <summary>Whether the displayed status describes a failure.</summary>
    private bool _isErrorStatus;

    /// <summary>Whether the owner uses GitHub authentication.</summary>
    private bool _isGitHubUser;

    /// <summary>Whether initial owned data is being loaded.</summary>
    private bool _isLoading = true;

    /// <summary>Current operation feedback.</summary>
    private string? _statusMessage;


    /// <summary>Current circuit authentication state.</summary>
    [Inject]
    private AuthenticationStateProvider AuthStateProvider { get; set; } = null!;


    /// <summary>Reads repositories through the owner GitHub connection.</summary>
    [Inject]
    private IGitHubService GitHubService { get; set; } = null!;

    /// <summary>Isolates EF-backed operations from the shared Blazor circuit lifetime.</summary>
    [Inject]
    private ScopedApplicationOperations Operations { get; set; } = null!;

    /// <summary>Reviewed operational logger for the component.</summary>
    [Inject]
    private ILogger<GitHubRepos> Logger { get; set; } = null!;


    /// <summary>
    ///     Cancels repository operations and releases the component cancellation source.
    /// </summary>
    public void Dispose()
    {
        _componentCancellation.Cancel();
        _componentCancellation.Dispose();
        GC.SuppressFinalize(this);
    }


    /// <summary>
    ///     On component initialization, we check the user's authentication state. If authenticated, we
    ///     attempt to find the user in our database. If the user is a GitHub user with a valid access
    ///     token, we fetch their repositories from GitHub. We also handle loading states and potential
    ///     issues with fetching repositories.
    /// </summary>
    /// <returns>A task representing completion of this operation, including any awaited callbacks.</returns>
    protected override async Task OnInitializedAsync()
    {
        try
        {
            var userDetails = await Operations.RunAsync<IUserService, AuthenticatedUserDetails>(users =>
                AuthenticatedUserResolver.GetCurrentUserDetailsAsync(
                    AuthStateProvider, users, _componentCancellation.Token));

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
    ///     When the user clicks the "Save" button on a repository card, this method is called. It
    ///     attempts to save the selected GitHub repository as a project in our system.
    /// </summary>
    /// <param name="repo">The DTO representing the GitHub repository to be saved.</param>
    /// <returns>A task representing completion of this operation, including any awaited callbacks.</returns>
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
            var success = await Operations.RunAsync<IApplicationService, bool>(apps =>
                apps.AddGitHubAppAsync(_currentUserId, repo.Name, repo.HtmlUrl, _componentCancellation.Token));

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
