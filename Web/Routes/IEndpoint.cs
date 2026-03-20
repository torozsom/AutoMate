namespace Web.Routes;

/// <summary>
///     An endpoint is a class that defines a set of routes for the application.
///     It is responsible for mapping the routes to the appropriate controllers and actions.
/// </summary>
public interface IEndpoint
{
    /// <summary>
    ///     Maps the routes defined by the endpoint to the route builder.
    /// </summary>
    /// <param name="app">The endpoint route builder to map the routes to.</param>
    void Map(IEndpointRouteBuilder app);
}