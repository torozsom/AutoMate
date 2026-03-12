using Web.Components;
using Web.Routes;

namespace Web.Routes.Endpoints;


/// <summary>
///     Endpoint for Razor Components. This will map the root path to
///     the App component, and enable interactive server render mode.
/// </summary>
public class RazorComponentsEndpoint : IEndpoint
{
    public void Map(IEndpointRouteBuilder app)
    {
        app.MapRazorComponents<App>().AddInteractiveServerRenderMode();
    }
}

