using Microsoft.AspNetCore.Components;
using Services.Docker;

namespace Web.Components.Layout;

public partial class MainLayout : LayoutComponentBase
{
    private bool? _isDockerRunning;
    [Inject] private IDockerService DockerService { get; set; } = null!;

    /// <summary>
    ///     On component initialization, check if the Docker Engine is responsive
    ///     by pinging it through the injected DockerService.
    /// </summary>
    protected override async Task OnInitializedAsync()
    {
        _isDockerRunning = await DockerService.PingAsync();
    }
}