using Application.Abstractions.Diagnostics.Metrics;
using Application.Data.Users;
using Microsoft.AspNetCore.Components;
using Web.Components.Pages.Identity;

namespace Web.Components.Shared.Metrics;

/// <summary>
///     Presents authorized automatic-collection, retention and sampling information.
/// </summary>
public partial class TelemetryPreferences
{
    /// <summary>Safe operation failure.</summary>
    private string? _error;

    /// <summary>Avoids repeated database reads for unchanged parent parameters.</summary>
    private Guid _loadedProject;

    /// <summary>Internal owner identity.</summary>
    private Guid _owner;

    /// <summary>Current authorized preferences.</summary>
    private DeploymentTelemetryPreferences? _preferences;

    /// <summary>Owner's project.</summary>
    [Parameter]
    public Guid ProjectId { get; set; }

    /// <summary>
    ///     Loads owner-authorized retention and sampling metadata for a changed project.
    /// </summary>
    /// <returns>A task representing completion of this operation, including any awaited callbacks.</returns>
    protected override async Task OnParametersSetAsync()
    {
        if (_loadedProject == ProjectId) return;
        _loadedProject = ProjectId;
        _preferences = null;
        try
        {
            await using var scope = ServiceScopes.CreateAsyncScope();
            _owner = await AuthenticatedUserResolver.GetCurrentUserIdAsync(Authentication,
                scope.ServiceProvider.GetRequiredService<IUserService>());
            if (_owner != Guid.Empty)
                _preferences = await scope.ServiceProvider
                    .GetRequiredService<IDeploymentHistoryService>().GetPreferencesAsync(_owner, ProjectId);
            _error = null;
        }
        catch (Exception)
        {
            _error = "Unable to load diagnostic collection preferences.";
        }
    }
}
