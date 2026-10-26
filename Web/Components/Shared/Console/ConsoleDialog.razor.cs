using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;

namespace Web.Components.Shared.Console;

/// <summary>
///     Hosts accessible dialog content with keyboard focus trapping and disposal-time focus restoration.
/// </summary>
public partial class ConsoleDialog
{
    /// <summary>Navigation cancels pending browser initialization.</summary>
    private readonly CancellationTokenSource _lifetime = new();

    /// <summary>Unique accessible heading identity.</summary>
    private readonly string _titleId = "dialog-" + Guid.NewGuid().ToString("N");

    /// <summary>Explicit keyboard-helper availability guidance.</summary>
    private bool _helpersUnavailable;

    /// <summary>Initialization is observed before disposal releases callback references.</summary>
    private Task? _initialization;

    /// <summary>Imported interaction module.</summary>
    private IJSObjectReference? _module;

    /// <summary>Callback lifetime owned by the component.</summary>
    private DotNetObjectReference<ConsoleDialog>? _reference;

    /// <summary>Disposable native event registration.</summary>
    private IJSObjectReference? _registration;

    /// <summary>Element used by the local focus trap.</summary>
    private ElementReference _root;

    /// <summary>Dialog heading.</summary>
    [Parameter]
    public string Title { get; set; } = "";

    /// <summary>Short context label.</summary>
    [Parameter]
    public string Eyebrow { get; set; } = "";

    /// <summary>Existing form and action content.</summary>
    [Parameter]
    public RenderFragment? ChildContent { get; set; }

    /// <summary>Cancellation action; no implicit submission.</summary>
    [Parameter]
    public EventCallback OnClose { get; set; }

    /// <summary>
    ///     Detaches browser focus handling and releases JS references without canceling server work.
    /// </summary>
    /// <returns>A task representing completion of component cleanup.</returns>
    public async ValueTask DisposeAsync()
    {
        if (_lifetime.IsCancellationRequested) return;
        await _lifetime.CancelAsync();
        if (_initialization is not null) await _initialization;
        try
        {
            if (_registration is not null)
            {
                await _registration.InvokeVoidAsync("dispose");
                await _registration.DisposeAsync();
            }

            if (_module is not null) await _module.DisposeAsync();
        }
        catch (JSDisconnectedException)
        {
            /* There is no connected DOM to restore after circuit loss. */
        }

        _reference?.Dispose();
        _lifetime.Dispose();
    }

    /// <summary>
    ///     Initializes the browser focus trap after the dialog first renders.
    /// </summary>
    /// <param name="firstRender">Whether this is the first interactive render.</param>
    /// <returns>A task representing completion of this operation, including any awaited callbacks.</returns>
    protected override Task OnAfterRenderAsync(bool firstRender)
    {
        return firstRender ? _initialization = InitializeAsync() : Task.CompletedTask;
    }

    /// <summary>
    ///     Attaches browser interactions only for the current dialog lifetime.
    /// </summary>
    /// <returns>A task representing completion of this operation, including any awaited callbacks.</returns>
    private async Task InitializeAsync()
    {
        try
        {
            _module = await JS.InvokeAsync<IJSObjectReference>("import", _lifetime.Token, "./js/console-ui.js");
            if (_lifetime.IsCancellationRequested) return;
            _reference = DotNetObjectReference.Create(this);
            _registration =
                await _module.InvokeAsync<IJSObjectReference>("attachDialog", _lifetime.Token, _root, _reference);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
        catch (JSDisconnectedException)
        {
            /* Circuit loss closes the browser interaction lifetime. */
        }
        catch (JSException error)
        {
            Logger.LogWarning("Dialog keyboard initialization failed: {FailureType}.", error.GetType().Name);
            if (!_lifetime.IsCancellationRequested)
            {
                _helpersUnavailable = true;
                StateHasChanged();
            }
        }
    }

    /// <summary>
    ///     Handles Escape from the browser focus trap.
    /// </summary>
    /// <returns>A task representing completion of this operation, including any awaited callbacks.</returns>
    [JSInvokable]
    public Task CloseDialog()
    {
        return _lifetime.IsCancellationRequested ? Task.CompletedTask : OnClose.InvokeAsync();
    }
}
