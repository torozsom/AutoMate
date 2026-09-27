using Application.Abstractions.Hosting;
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;

namespace Web.Components.Layout;

public partial class NavMenu : ComponentBase
{
    private bool _isDarkMode;

    [Inject] private IJSRuntime JS { get; set; } = null!;

    [Inject] private ILogger<NavMenu> Logger { get; set; } = null!;

    [Inject]
    private IDeploymentCapabilities DeploymentCapabilities { get; set; } = null!;

    /// <summary>
    ///     Restores the theme preference after the interactive connection is available.
    /// </summary>
    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (!firstRender) return;

        try
        {
            var theme = await JS.InvokeAsync<string>("window.getTheme");
            _isDarkMode = theme == "dark";
            StateHasChanged();
        }
        catch (JSException ex)
        {
            Logger.LogDebug(ex, "Could not read theme preference from local storage.");
            _isDarkMode = false;
        }
    }

    /// <summary>Toggles and persists the selected colour theme.</summary>
    private async Task ToggleTheme()
    {
        _isDarkMode = !_isDarkMode;
        var theme = _isDarkMode ? "dark" : "light";

        try
        {
            await JS.InvokeVoidAsync("window.setTheme", theme);
        }
        catch (JSException ex)
        {
            Logger.LogDebug(ex, "Could not persist theme preference.");
        }
    }
}
