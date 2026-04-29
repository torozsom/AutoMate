using System.Security.Claims;
using Core.DTO;
using Core.Entities;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.EntityFrameworkCore;
using Services.Data;
using Services.Projects;
using Services.Scanner;

namespace Web.Components.Pages;

/// <summary>
///     The LocalGitRepos component is responsible for displaying a list of local Git projects
///     that have been scanned and saved to the user's account. It also allows the user to search
///     for and save new projects.
/// </summary>
public partial class LocalGitRepos : ComponentBase
{
    private bool _hasScanned;
    private bool _isErrorStatus;
    private bool _isScanning;
    private string _lastSearchedPath = string.Empty;


    /// This list holds the results of the local Git projects found during the scanning process.
    private List<LocalProjectDto>? _localProjects;

    private string _searchPath = string.Empty;
    private string? _statusMessage;

    [Inject] private AuthenticationStateProvider AuthStateProvider { get; set; } = null!;

    [Inject] private ILocalSystemScannerService SystemScannerService { get; set; } = null!;

    [Inject] private IProjectService ProjectService { get; set; } = null!;

    [Inject] private IServiceProvider ServiceProvider { get; set; } = null!;

    /// A computed property that determines whether the "Scan" button should be disabled.
    private bool IsScanButtonDisabled => string.IsNullOrWhiteSpace(_searchPath) || _isScanning;


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

        try
        {
            _localProjects = await SystemScannerService.ScanForProjectsAsync(_searchPath);
        }
        catch (Exception ex)
        {
            _localProjects = [];
            SetStatusMessage("An error occurred during scanning. Check the provided path." +
                             "Error details: " + ex.Message, true);
        }
        finally
        {
            _isScanning = false;
            _hasScanned = true;
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
    ///     Saves the selected project to the user's account. It checks if the user is authenticated,
    ///     retrieves the user's ID, and then calls the project service to save the project.
    ///     It also updates the status message based on the success of the operation.
    /// </summary>
    /// <param name="project">The DTO of the Local Project to be saved.</param>
    /// <param name="csproject">The DTO of the CsProject to be saved</param>
    private async Task SaveProjectAsync(LocalProjectDto project, CsProjectDto csproject)
    {
        var userId = await GetCurrentUserIdAsync();

        if (userId == Guid.Empty)
        {
            SetStatusMessage("You need to be logged in to save projects.", true);
            return;
        }

        // Attempt to save the project to the user's account
        var success = await ProjectService.AddLocalProjectAsync(userId, project, csproject);

        if (success)
            SetStatusMessage($"{csproject.Name} successfully saved!", false);
        else
            SetStatusMessage($"{csproject.Name} already exists in your workspace!", true);
    }


    /// <summary>
    ///     Helper method to extract and resolve the current user's database ID.
    ///     Encapsulates the AuthState checking and DB fallback logic.
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

        // Fallback for GitHub users where the claim is an AccountId (string), not a GUID
        if (!string.IsNullOrEmpty(userIdString))
        {
            using var scope = ServiceProvider.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AutoMateDbContext>();
            var dbUser = await db.Users.OfType<GitHubUser>().FirstOrDefaultAsync(u => u.AccountId == userIdString);

            return dbUser?.Id ?? Guid.Empty;
        }

        return Guid.Empty;
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
    ///     This can be used before starting a new scan or when the user changes
    ///     the search path to ensure that old messages don't persist.
    /// </summary>
    private void ClearStatusMessage()
    {
        _statusMessage = null;
        _isErrorStatus = false;
    }
}