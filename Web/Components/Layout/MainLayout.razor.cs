using Microsoft.AspNetCore.Components;
using Services.Docker;

namespace Web.Components.Layout;

public partial class MainLayout : LayoutComponentBase
{
    private bool _hasCheckedDocker;

    private bool? _isDockerRunning;

    [Inject] private IDockerService DockerService { get; set; } = null!;


    /// <summary>
    ///     We use OnAfterRenderAsync for external service pings instead of OnInitializedAsync.
    ///     This ensures that the Blazor application loads instantly and displays the "Loading..." UI
    ///     without waiting for a potentially slow Docker socket timeout.
    /// </summary>
    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (firstRender && !_hasCheckedDocker)
        {
            _hasCheckedDocker = true;
            await CheckDockerStatusAsync();
        }
    }


    /// <summary>
    ///     Safely pings the Docker engine and updates the UI state.
    /// </summary>
    private async Task CheckDockerStatusAsync()
    {
        try
        {
            _isDockerRunning = await DockerService.PingAsync();
        }
        catch
        {
            // If the PingAsync method throws an exception (e.g., socket closed, access denied),
            // we safely default to the offline state instead of crashing the layout.
            _isDockerRunning = false;
        }
        finally
        {
            // Notify Blazor that the background ping finished and the UI needs to be updated
            StateHasChanged();
        }
    }
}