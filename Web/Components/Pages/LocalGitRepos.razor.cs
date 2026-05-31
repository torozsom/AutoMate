using System.Security.Claims;
using Core.DTO;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Web;
using Services.Data.Apps;
using Services.Data.Users;
using Services.Scanner;

namespace Web.Components.Pages;

/// <summary>
///     The LocalGitRepos component is responsible for displaying a list of local Git projects
///     that have been scanned and saved to the user's account. It also allows the user to search
///     for and save new projects.
/// </summary>
public partial class LocalGitRepos : ComponentBase, IDisposable
{
    /// A cancellation token source to manage asynchronous operations and ensure they are cancelled when the component is disposed.
    private readonly CancellationTokenSource _componentCancellation = new();

    /// A set to keep track of the paths of projects that are currently being saved, to prevent duplicate save operations.
    private readonly HashSet<string> _savingProjectPaths = new(StringComparer.OrdinalIgnoreCase);

    /// The current user's identifier.
    private Guid _currentUserId;

    /// A flag to indicate if the scanning process has been completed at least once.
    private bool _hasScanned;

    /// A flag to indicate if the last scanned operation resulted in an error.
    private bool _isErrorStatus;

    /// A flag to indicate if the scanning process is currently in progress.
    private bool _isScanning;

    /// The path of the last folder scanned by the system.
    private string _lastSearchedPath = string.Empty;

    /// This list holds the results of the local Git projects found during the scanning process.
    private List<LocalProjectDto>? _localProjects;

    /// The path to the folder where the scanning process will begin.
    private string _searchPath = string.Empty;

    /// A message to display to the user, indicating the status of the last operation.
    private string? _statusMessage;


    /// Authentication State Provider for checking user authentication and retrieving user information.
    [Inject]
    private AuthenticationStateProvider AuthStateProvider { get; set; } = null!;

    /// Service for scanning local projects for Git repositories.
    [Inject]
    private ILocalSystemScannerService SystemScannerService { get; set; } = null!;

    /// Service for managing apps, including fetching, creating, and deleting apps associated with users.
    [Inject]
    private IApplicationService ApplicationService { get; set; } = null!;

    /// Service for managing user accounts.
    [Inject]
    private IUserService UserService { get; set; } = null!;

    /// Logger for logging errors and important information within the component.
    [Inject]
    private ILogger<LocalGitRepos> Logger { get; set; } = null!;


    /// A computed property that determines whether the "Scan" button should be disabled.
    private bool IsScanButtonDisabled => string.IsNullOrWhiteSpace(_searchPath) || _isScanning;


    /// Disposes of the component, ensuring that any ongoing operations are cancelled and resources are released.
    public void Dispose()
    {
        _componentCancellation.Cancel();
        _componentCancellation.Dispose();
        GC.SuppressFinalize(this);
    }


    /// <summary>
    ///     Lifecycle method that runs on component initialization.
    ///     Resolves the current user's ID ahead of time.
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
                var (userId, _, _) = await UserService.GetUserDetailsFromIdentifierAsync(nameIdentifier,
                    _componentCancellation.Token);
                _currentUserId = userId;
            }
        }
    }


    /// <summary>
    ///     Starts the scanning process for Git projects in the specified folder.
    ///     It updates the UI state accordingly and handles any exceptions that may occur during scanning.
    /// </summary>
    private async Task StartScanAsync()
    {
        if (string.IsNullOrWhiteSpace(_searchPath)) return;

        _isScanning = true;
        _hasScanned = false;
        _lastSearchedPath = _searchPath;
        _localProjects = null;
        ClearStatusMessage();
        var wasCancelled = false;

        try
        {
            _localProjects = await SystemScannerService.ScanForProjectsAsync(_searchPath,
                _componentCancellation.Token);
        }
        catch (OperationCanceledException)
        {
            wasCancelled = true;
            _localProjects = [];
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to scan local repositories at {SearchPath}.", _searchPath);
            _localProjects = [];
            SetStatusMessage("An error occurred during scanning. Check the provided path and try again.", true);
        }
        finally
        {
            _isScanning = false;
            _hasScanned = !wasCancelled;

            if (!_componentCancellation.IsCancellationRequested)
                StateHasChanged();
        }
    }


    /// <summary>
    ///     Handles the key up event on the input field. If the Enter key is pressed
    ///     and the search path is valid, it triggers the scanning process.
    /// </summary>
    /// <param name="e">The keyboard event arguments, used to check for the Enter key.</param>
    private async Task HandleKeyUp(KeyboardEventArgs e)
    {
        if (e.Key == "Enter" && !IsScanButtonDisabled)
            await StartScanAsync();
    }


    /// <summary>
    ///     Saves the selected app to the user's account.
    ///     It updates the status message based on the success of the operation.
    /// </summary>
    /// <param name="project">The DTO of the Local Project to be saved.</param>
    /// <param name="csproject">The DTO of the CsProject to be saved</param>
    private async Task SaveProjectAsync(LocalProjectDto project, CsProjectDto csproject)
    {
        if (_currentUserId == Guid.Empty)
        {
            SetStatusMessage("You need to be logged in to save projects.", true);
            return;
        }

        if (!_savingProjectPaths.Add(csproject.Path))
            return;

        try
        {
            var success = await ApplicationService.AddLocalAppAsync(_currentUserId, project, csproject,
                _componentCancellation.Token);

            if (success)
                SetStatusMessage($"{csproject.Name} successfully saved!", false);
            else
                SetStatusMessage($"{csproject.Name} already exists in your workspace!", true);
        }
        catch (OperationCanceledException)
        {
            // Component was disposed while saving.
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to save local project {ProjectPath}.", csproject.Path);
            SetStatusMessage("Failed to save the project. Please try again later.", true);
        }
        finally
        {
            _savingProjectPaths.Remove(csproject.Path);
        }
    }


    private bool IsSaving(CsProjectDto project)
    {
        return _savingProjectPaths.Contains(project.Path);
    }


    /// <summary>
    ///     Sets the status message to be displayed to the user, along with an indication
    ///     of whether it's an error message or not.
    /// </summary>
    /// <param name="message"></param>
    /// <param name="isError"></param>
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
