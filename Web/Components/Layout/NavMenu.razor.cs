using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;

namespace Web.Components.Layout;

public partial class NavMenu : ComponentBase
{
    private bool _isDarkMode;

    [Inject]
    private IJSRuntime JS { get; set; } = null!;

    [Inject]
    private ILogger<NavMenu> Logger { get; set; } = null!;


    /// <summary>
    ///     On the first render, we check the user's theme preference from
    ///     local storage and set the initial state of the theme toggle button accordingly.
    /// </summary>
    /// <param name="firstRender">A boolean value indicating whether this is the first time the component is being rendered.</param>
    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (firstRender)
            try
            {
                // Safely invoke JS
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
    ///     This method toggles the theme between light and dark modes. It updates the
    ///     _isDarkMode field and calls a JavaScript function to apply the selected theme.
    ///     The JavaScript function is expected to handle the actual theme switching logic,
    ///     such as adding or removing CSS classes or updating the document's data attributes.
    /// </summary>
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
