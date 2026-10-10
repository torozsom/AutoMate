using Application.Abstractions.Hosting;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.JSInterop;

namespace Web.Components.Layout;

public partial class NavMenu : ComponentBase
{
    private bool _isDarkMode;

    /// <summary>Mobile navigation visibility.</summary>
    private bool _menuOpen;

    /// <summary>Mobile toggle receives focus after Escape.</summary>
    private ElementReference _menuToggle;

    [Inject] private IJSRuntime JS { get; set; } = null!;

    [Inject] private ILogger<NavMenu> Logger { get; set; } = null!;

    [Inject] private IDeploymentCapabilities DeploymentCapabilities { get; set; } = null!;

    /// <summary>Toggles the mobile navigation disclosure.</summary>
    private void ToggleMenu()
    {
        _menuOpen = !_menuOpen;
    }

    /// <summary>Closes navigation after selecting a destination.</summary>
    private void CloseMenu()
    {
        _menuOpen = false;
    }

    /// <summary>Escape dismisses the mobile navigation.</summary>
    private async Task HandleMenuKey(KeyboardEventArgs args)
    {
        if (args.Key == "Escape")
        {
            CloseMenu();
            await _menuToggle.FocusAsync();
        }
    }

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