using Application.Abstractions.Hosting;
using Application.Abstractions.Scanning;
using Application.Data.Apps;
using Application.Data.Users;
using Domain.DTO;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Web;

namespace Web.Components.Pages;

/// <summary>
///     The LocalGitRepos component is responsible for displaying a list of local Git projects that
///     have been scanned and saved to the user's account. It also allows the user to search for and
///     save new projects.
/// </summary>
public partial class LocalGitRepos : ComponentBase, IDisposable
{
    /// <summary>Cancels operations when the component is disposed.</summary>
    private readonly CancellationTokenSource _componentCancellation = new();

    /// <summary>Local project paths currently being saved.</summary>
    private readonly HashSet<string> _savingProjectPaths = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Resolved internal owner identity.</summary>
    private Guid _currentUserId;

    /// <summary>Whether a local scan has completed.</summary>
    private bool _hasScanned;

    /// <summary>Whether the displayed status describes a failure.</summary>
    private bool _isErrorStatus;

    /// <summary>Whether local discovery is in progress.</summary>
    private bool _isScanning;

    /// <summary>Directory used by the last local scan.</summary>
    private string _lastSearchedPath = string.Empty;

    /// <summary>Discovered local projects, or null before scanning.</summary>
    private List<LocalProjectDto>? _localProjects;

    /// <summary>User-entered local scan directory.</summary>
    private string _searchPath = string.Empty;

    /// <summary>Current operation feedback.</summary>
    private string? _statusMessage;


    /// <summary>Current circuit authentication state.</summary>
    [Inject]
    private AuthenticationStateProvider AuthStateProvider { get; set; } = null!;

    /// <summary>Discovers local source projects for the selected directory.</summary>
    [Inject]
    private ILocalSystemScannerService SystemScannerService { get; set; } = null!;

    /// <summary>Server-enforced hosting-profile capability flags.</summary>
    [Inject]
    private IDeploymentCapabilities DeploymentCapabilities { get; set; } = null!;

    /// <summary>Owner-scoped saved-project operations.</summary>
    [Inject]
    private IApplicationService ApplicationService { get; set; } = null!;

    /// <summary>Resolves owner accounts and protected provider connections.</summary>
    [Inject]
    private IUserService UserService { get; set; } = null!;

    /// <summary>Reviewed operational logger for the component.</summary>
    [Inject]
    private ILogger<LocalGitRepos> Logger { get; set; } = null!;


    /// <summary>Whether missing input or an active scan disables discovery.</summary>
    private bool IsScanButtonDisabled => string.IsNullOrWhiteSpace(_searchPath) || _isScanning;


    /// <summary>
    ///     Cancels local repository operations and releases the component cancellation source.
    /// </summary>
    public void Dispose()
    {
        _componentCancellation.Cancel();
        _componentCancellation.Dispose();
        GC.SuppressFinalize(this);
    }


    /// <summary>
    ///     Lifecycle method that runs on component initialization. Resolves the current user's ID ahead
    ///     of time.
    /// </summary>
    /// <returns>A task representing completion of this operation, including any awaited callbacks.</returns>
    protected override async Task OnInitializedAsync()
    {
        if (!DeploymentCapabilities.LocalDeploymentsEnabled)
        {
            _statusMessage = "Local filesystem access is available only in a self-hosted AutoMate installation.";
            _isErrorStatus = true;
            return;
        }

        _currentUserId = await AuthenticatedUserResolver.GetCurrentUserIdAsync(
            AuthStateProvider,
            UserService,
            _componentCancellation.Token);
    }


    /// <summary>
    ///     Starts the scanning process for Git projects in the specified folder. It updates the UI state
    ///     accordingly and handles any exceptions that may occur during scanning.
    /// </summary>
    /// <returns>A task representing completion of this operation, including any awaited callbacks.</returns>
    private async Task StartScanAsync()
    {
        if (!DeploymentCapabilities.LocalDeploymentsEnabled)
        {
            SetStatusMessage("Local filesystem access is disabled for this AutoMate instance.", true);
            return;
        }

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
    ///     Handles the key up event on the input field. If the Enter key is pressed and the search path
    ///     is valid, it triggers the scanning process.
    /// </summary>
    /// <param name="e">The keyboard event arguments, used to check for the Enter key.</param>
    /// <returns>A task representing completion of this operation, including any awaited callbacks.</returns>
    private async Task HandleKeyUp(KeyboardEventArgs e)
    {
        if (e.Key == "Enter" && !IsScanButtonDisabled)
            await StartScanAsync();
    }


    /// <summary>
    ///     Saves the selected app to the user's account. It updates the status message based on the
    ///     success of the operation.
    /// </summary>
    /// <param name="project">The DTO of the Local Project to be saved.</param>
    /// <param name="csproject">The DTO of the CsProject to be saved</param>
    /// <returns>A task representing completion of this operation, including any awaited callbacks.</returns>
    private async Task SaveProjectAsync(LocalProjectDto project, CsProjectDto csproject)
    {
        if (!DeploymentCapabilities.LocalDeploymentsEnabled)
        {
            SetStatusMessage("Local projects cannot be saved in this AutoMate instance.", true);
            return;
        }

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


    /// <summary>
    ///     Indicates whether the local project is currently being saved.
    /// </summary>
    /// <param name="project">The C# project to inspect.</param>
    /// <returns><see langword="true" /> when a save operation is already active for the project.</returns>
    private bool IsSaving(CsProjectDto project)
    {
        return _savingProjectPaths.Contains(project.Path);
    }


    /// <summary>
    ///     Sets the status message to be displayed to the user, along with an indication of whether it's
    ///     an error message or not.
    /// </summary>
    /// <param name="message">The message to display.</param>
    /// <param name="isError">Indicates whether the message represents an error.</param>
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