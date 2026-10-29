using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;

namespace Web.Components.Shared.Projects;

/// <summary>
///     Presents project section links with scoped scroll tracking and fragment restoration.
/// </summary>
public partial class ProjectSectionNavigation
{
    /// <summary>Disposable browser listeners for this page instance.</summary>
    private IJSObjectReference? _controller;

    /// <summary>Scoped browser module with no circuit business state.</summary>
    private IJSObjectReference? _module;

    /// <summary>Root for section navigation and the terminal shortcut.</summary>
    private ElementReference _navigation;

    /// <summary>
    ///     Detaches navigation listeners and releases its JS references.
    /// </summary>
    /// <returns>A task representing completion of component cleanup.</returns>
    public async ValueTask DisposeAsync()
    {
        try
        {
            if (_controller is not null)
            {
                await _controller.InvokeVoidAsync("dispose");
                await _controller.DisposeAsync();
            }

            if (_module is not null) await _module.DisposeAsync();
        }
        catch (JSDisconnectedException)
        {
        }
    }

    /// <summary>
    ///     Attaches scoped section navigation after the first interactive render.
    /// </summary>
    /// <param name="firstRender">Whether this is the first interactive render.</param>
    /// <returns>A task representing completion of this operation, including any awaited callbacks.</returns>
    /// <exception cref="Microsoft.JSInterop.JSException">
    ///     The browser interaction fails or the interactive circuit is
    ///     disconnected.
    /// </exception>
    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (!firstRender) return;
        _module = await Js.InvokeAsync<IJSObjectReference>("import", "./js/project-sections.js");
        _controller = await _module.InvokeAsync<IJSObjectReference>("attach", _navigation);
    }
}
