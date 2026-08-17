namespace Web.Routes.Endpoints;

/// <summary>
///     Endpoint for serving static assets. Registered before other endpoints
///     to ensure static files are served correctly and performantly.
/// </summary>
public sealed class StaticAssetsEndpoint : IEndpoint
{
    /// <inheritdoc />
    public void Map(IEndpointRouteBuilder app)
    {
        // AllowAnonymous ensures static files don't trigger the auth pipeline unnecessarily
        app.MapStaticAssets().AllowAnonymous();
    }
}