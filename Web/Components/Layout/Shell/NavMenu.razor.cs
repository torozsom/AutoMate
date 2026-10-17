using Application.Abstractions.Hosting;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.JSInterop;

namespace Web.Components.Layout.Shell;

/// <summary>
///     Renders responsive navigation and persists the selected browser theme.
/// </summary>
public partial class NavMenu : ComponentBase
{
    /// <summary>Whether the current browser theme is dark.</summary>
    private bool _isDarkMode = true;

    /// <summary>Mobile navigation visibility.</summary>
    private bool _menuOpen;

    /// <summary>Mobile toggle receives focus after Escape.</summary>
    private ElementReference _menuToggle;

    /// <summary>Browser interop for owned UI interactions.</summary>
    [Inject]
    private IJSRuntime JS { get; set; } = null!;

    /// <summary>Reviewed operational logger for the component.</summary>
    [Inject]
    private ILogger<NavMenu> Logger { get; set; } = null!;

    /// <summary>Server-enforced hosting-profile capability flags.</summary>
    [Inject]
    private IDeploymentCapabilities DeploymentCapabilities { get; set; } = null!;

    /// <summary>
    ///     Toggles the mobile navigation disclosure.
    /// </summary>
    private void ToggleMenu()
    {
        _menuOpen = !_menuOpen;
    }

    /// <summary>
    ///     Closes navigation after selecting a destination.
    /// </summary>
    private void CloseMenu()
    {
        _menuOpen = false;
    }

    /// <summary>
    ///     Escape dismisses the mobile navigation.
    /// </summary>
    /// <param name="args">Browser event data for this interaction.</param>
    /// <returns>A task representing completion of this operation, including any awaited callbacks.</returns>
    /// <exception cref="Microsoft.JSInterop.JSException">
    ///     The browser interaction fails or the interactive circuit is
    ///     disconnected.
    /// </exception>
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
    /// <param name="firstRender">Whether this is the first interactive render.</param>
    /// <returns>A task representing completion of this operation, including any awaited callbacks.</returns>
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

    /// <summary>
    ///     Toggles and persists the selected colour theme.
    /// </summary>
    /// <returns>A task representing completion of this operation, including any awaited callbacks.</returns>
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
