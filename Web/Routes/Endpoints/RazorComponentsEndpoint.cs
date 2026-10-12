using Web.Components;

namespace Web.Routes.Endpoints;

/// <summary>
///     Endpoint for Razor Components. Maps the root path to the App component and enables interactive
///     server render mode.
/// </summary>
public sealed class RazorComponentsEndpoint : IEndpoint
{
    /// <summary>
    ///     Maps the application root components with interactive server rendering.
    /// </summary>
    /// <param name="app">Endpoint route builder receiving these routes.</param>
    public void Map(IEndpointRouteBuilder app)
    {
        app.MapRazorComponents<App>().AddInteractiveServerRenderMode();
    }
}