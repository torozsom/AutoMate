using Web.Components;

namespace Web.Routes.Endpoints;

/// <summary>
///     Endpoint for Razor Components. Maps the root path to the App component
///     and enables interactive server render mode.
/// </summary>
public class RazorComponentsEndpoint : IEndpoint
{
    /// <inheritdoc />
    public void Map(IEndpointRouteBuilder app)
    {
        app.MapRazorComponents<App>().AddInteractiveServerRenderMode();
    }
}