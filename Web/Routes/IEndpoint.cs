namespace Web.Routes;


/// <summary>
///     An endpoint is a class that defines a set of routes for the application.
///     It is responsible for mapping the routes to the appropriate controllers and actions.
/// </summary>
public interface IEndpoint
{
    void Map(IEndpointRouteBuilder app);
}

