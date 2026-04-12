using Microsoft.AspNetCore.Components;
using Services.Docker;

namespace Web.Components.Layout;

public partial class MainLayout : LayoutComponentBase
{
    [Inject] private IDockerService DockerService { get; set; } = null!;

    private bool? _isDockerRunning;

    /// <summary>
    ///     On component initialization, check if the Docker Engine is responsive
    ///     by pinging it through the injected DockerService.
    /// </summary>
    protected override async Task OnInitializedAsync()
    {
        _isDockerRunning = await DockerService.PingAsync();
    }
}
