using Web.Routes;

namespace Web.Extensions;


/// <summary>
///     Extension methods for registering endpoints in the dependency injection container.
///     Endpoints are classes that implement the IEndpoint interface and define how to map routes to handlers.
/// </summary>
public static class EndpointExtensions
{
    /// <summary>
    ///     Registers an endpoint of type T in the dependency injection container.
    ///     The endpoint must implement the IEndpoint interface.
    /// </summary>
    /// <param name="services"></param>
    /// <typeparam name="T"></typeparam>
    /// <returns></returns>
    public static IServiceCollection AddEndpoint<T>(this IServiceCollection services) 
        where T : class, IEndpoint
    {
        services.AddTransient<IEndpoint, T>();
        return services;
    }
}

