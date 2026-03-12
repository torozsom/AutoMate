using Web.Routes;

namespace Web.Routes.Endpoints;


/// <summary>
///     Endpoint for serving static assets. This should be registered before
///     any other endpoints to ensure that static files are served correctly.
/// </summary>
public class StaticAssetsEndpoint : IEndpoint
{
    public void Map(IEndpointRouteBuilder app)
    {
        app.MapStaticAssets();
    }
}

