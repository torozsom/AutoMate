using System.Diagnostics;
using Microsoft.AspNetCore.Components;

namespace Web.Components.Pages;


/// <summary>
/// Represents an error page component in a Blazor application.
/// </summary>
/// <remarks>
/// This component is responsible for displaying error information. It initializes
/// the RequestId property, which can be used to correlate errors with specific requests.
/// </remarks>
public partial class Error : ComponentBase
{
    [CascadingParameter]
    private HttpContext? HttpContext { get; set; }

    private string? RequestId { get; set; }
    private bool ShowRequestId => !string.IsNullOrEmpty(RequestId);

    /// <summary>
    ///     Initializes the component by setting the RequestId based on the current activity or HTTP context trace identifier.
    /// </summary>
    protected override void OnInitialized() =>
        RequestId = Activity.Current?.Id ?? HttpContext?.TraceIdentifier;
}
