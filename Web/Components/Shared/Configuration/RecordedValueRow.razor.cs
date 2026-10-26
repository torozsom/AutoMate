using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;

namespace Web.Components.Shared.Configuration;

/// <summary>
///     Presents a recorded metadata value and an optional browser clipboard action.
/// </summary>
public partial class RecordedValueRow
{
    /// <summary>Clipboard feedback.</summary>
    private bool _copied;

    /// <summary>Clipboard availability guidance.</summary>
    private bool _failed;

    /// <summary>Recorded field label.</summary>
    [Parameter]
    public string Label { get; set; } = "";

    /// <summary>Non-secret recorded metadata.</summary>
    [Parameter]
    public string? Value { get; set; }

    /// <summary>Long artifacts can be copied without truncating their displayed values.</summary>
    [Parameter]
    public bool Copyable { get; set; }

    /// <summary>
    ///     Copies explicit metadata through the local browser API.
    /// </summary>
    /// <returns>A task completing after copying or setting local clipboard-failure feedback.</returns>
    private async Task CopyAsync()
    {
        try
        {
            await using var module = await JS.InvokeAsync<IJSObjectReference>("import", "./js/console-ui.js");
            await module.InvokeVoidAsync("copy", Value);
            _copied = true;
            _failed = false;
        }
        catch (JSException)
        {
            _failed = true;
        }
    }
}
