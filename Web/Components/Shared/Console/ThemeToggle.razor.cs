using Microsoft.JSInterop;

namespace Web.Components.Shared.Console;

/// <summary>
///     Presents a theme action using the existing browser theme-storage contract.
/// </summary>
public partial class ThemeToggle
{
    /// <summary>
    ///     Preserves the existing local theme storage contract.
    /// </summary>
    /// <returns>A task representing completion of this operation, including any awaited callbacks.</returns>
    /// <exception cref="Microsoft.JSInterop.JSException">
    ///     The theme or local-storage operation fails; circuit disconnects are
    ///     ignored.
    /// </exception>
    private async Task ToggleAsync()
    {
        try
        {
            var current = await Js.InvokeAsync<string>("window.getTheme");
            await Js.InvokeVoidAsync("window.setTheme", current == "dark" ? "light" : "dark");
        }
        catch (JSDisconnectedException)
        {
        }
    }
}
