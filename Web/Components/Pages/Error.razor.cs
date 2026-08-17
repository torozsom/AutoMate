using System.Diagnostics;
using Microsoft.AspNetCore.Components;

namespace Web.Components.Pages;

/// <summary>
///     Represents an error page component in a Blazor application.
/// </summary>
/// <remarks>
///     This component is responsible for displaying error information. It initializes
///     the RequestId property, which can be used to correlate errors with specific requests.
/// </remarks>
public partial class Error : ComponentBase
{
    /// <summary>
    ///     The current HTTP context supplied by the Blazor host for request correlation.
    /// </summary>
    [CascadingParameter]
    private HttpContext? HttpContext { get; set; }

    /// <summary>
    ///     The request identifier shown to correlate the rendered error page with server logs.
    /// </summary>
    private string? RequestId { get; set; }

    /// <summary>
    ///     Indicates whether a request identifier is available for display.
    /// </summary>
    private bool ShowRequestId => !string.IsNullOrEmpty(RequestId);

    /// <summary>
    ///     Initializes the component by setting the RequestId based on the current activity or HTTP context trace identifier.
    /// </summary>
    protected override void OnInitialized()
    {
        RequestId = Activity.Current?.Id ?? HttpContext?.TraceIdentifier;
    }
}