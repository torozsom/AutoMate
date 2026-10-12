using Application.Abstractions.Docker;
using Application.Abstractions.Hosting;
using Microsoft.AspNetCore.Components;

namespace Web.Components.Layout;

/// <summary>
///     Renders the shared application shell and checks local Docker availability after interactive
///     rendering.
/// </summary>
public partial class MainLayout : LayoutComponentBase
{
    /// <summary>Whether the interactive local Docker probe has already started.</summary>
    private bool _hasCheckedDocker;

    /// <summary>Observed Docker availability, or null before the probe completes.</summary>
    private bool? _isDockerRunning;

    /// <summary>Hosting capabilities gate local connectivity checks.</summary>
    [Inject]
    private IDeploymentCapabilities DeploymentCapabilities { get; set; } = null!;

    /// <summary>Local Docker operations permitted by the hosting profile.</summary>
    [Inject]
    private IDockerService DockerService { get; set; } = null!;

    /// <summary>Reviewed operational logger for the component.</summary>
    [Inject]
    private ILogger<MainLayout> Logger { get; set; } = null!;


    /// <summary>
    ///     Checks local Docker availability after the first interactive render so initial page rendering
    ///     is not blocked.
    /// </summary>
    /// <param name="firstRender">Whether this is the first interactive render.</param>
    /// <returns>A task representing completion of this operation, including any awaited callbacks.</returns>
    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (firstRender && !_hasCheckedDocker && DeploymentCapabilities.LocalDeploymentsEnabled)
        {
            _hasCheckedDocker = true;
            await CheckDockerStatusAsync();
        }
    }


    /// <summary>
    ///     Safely pings the Docker engine and updates the UI state.
    /// </summary>
    /// <returns>A task representing completion of this operation, including any awaited callbacks.</returns>
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