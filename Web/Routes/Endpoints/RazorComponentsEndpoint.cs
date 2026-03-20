using Web.Components;

namespace Web.Routes.Endpoints;

/// <summary>
///     Endpoint for Razor Components. This will map the root path to
///     the App component, and enable interactive server render mode.
/// </summary>
public class RazorComponentsEndpoint : IEndpoint
{
    /// <inheritdoc />
    public void Map(IEndpointRouteBuilder app)
    {
        app.MapRazorComponents<App>().AddInteractiveServerRenderMode();
    }
}