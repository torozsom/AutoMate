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

    private List<LocalProjectDto>? _localProjects;


    private string _searchPath = string.Empty;

    private string? _statusMessage;

    [Inject] private AuthenticationStateProvider AuthStateProvider { get; set; } = null!;

    [Inject] private ILocalScannerService ScannerService { get; set; } = null!;

    [Inject] private IProjectService ProjectService { get; set; } = null!;

    [Inject] private IServiceProvider ServiceProvider { get; set; } = null!;


    /// <summary>
    ///     Starts the scanning process for Git projects in the specified folder.
    ///     It updates the UI state accordingly and handles any exceptions that may occur during scanning.
    /// </summary>
    private async Task StartScan()
    {
        if (string.IsNullOrWhiteSpace(_searchPath)) return;

        _isScanning = true;
        _hasScanned = false;
        _lastSearchedPath = _searchPath;
        _localProjects = null;

        StateHasChanged();

        try
        {
            _localProjects = await Task.Run(() => ScannerService.ScanForProjectsAsync(_searchPath));
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error during scanning: {ex.Message}");
            _localProjects = [];
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
        if (e.Key == "Enter" && !string.IsNullOrWhiteSpace(_searchPath) && !_isScanning)
            await StartScan();
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
        var authState = await AuthStateProvider.GetAuthenticationStateAsync();
        var user = authState.User;

        if (!user.Identity?.IsAuthenticated ?? true)
        {
            _statusMessage = "You need to be logged in to save projects.";
            _isErrorStatus = true;
            return;
        }

        var userIdString = user.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        var userId = Guid.Empty;

        if (Guid.TryParse(userIdString, out var parsedId))
        {
            userId = parsedId;
        }
        else if (!string.IsNullOrEmpty(userIdString))
        {
            using var scope = ServiceProvider.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AutoMateDbContext>();
            var dbUser = await db.Users.OfType<GitHubUser>().FirstOrDefaultAsync(u => u.AccountId == userIdString);

            if (dbUser != null)
                userId = dbUser.Id;
        }

        if (userId == Guid.Empty)
            return;

        var success = await ProjectService.AddLocalProjectAsync(userId, project, csproject);

        if (success)
        {
            _statusMessage = $"{csproject.Name} successfully saved!";
            _isErrorStatus = false;
        }
        else
        {
            _statusMessage = $"{csproject.Name} already exists!";
            _isErrorStatus = true;
        }
    }
}