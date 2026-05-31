using Microsoft.AspNetCore.Components;
using Services.Docker;

namespace Web.Components.Layout;

public partial class MainLayout : LayoutComponentBase
{
    private bool _hasCheckedDocker;
    private bool? _isDockerRunning;

    [Inject]
    private IDockerService DockerService { get; set; } = null!;

    [Inject]
    private ILogger<MainLayout> Logger { get; set; } = null!;


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
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Docker status check failed.");
            _isDockerRunning = false;
        }
        finally
        {
            await InvokeAsync(StateHasChanged);
        }
    }
}
