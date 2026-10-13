namespace Web.Routes.Endpoints;

/// <summary>
///     Endpoint for serving static assets. Registered before other endpoints to ensure static files
///     are served correctly and performantly.
/// </summary>
public sealed class StaticAssetsEndpoint : IEndpoint
{
    /// <summary>
    ///     Maps anonymous static-asset endpoints.
    /// </summary>
    /// <param name="app">Endpoint route builder receiving these routes.</param>
    public void Map(IEndpointRouteBuilder app)
    {
        // AllowAnonymous ensures static files don't trigger the auth pipeline unnecessarily
        app.MapStaticAssets().AllowAnonymous();
    }
}
