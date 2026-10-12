using System.Reflection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Web.Routes;

namespace Web.Extensions;

/// <summary>
///     Extension methods for auto-discovering and registering endpoints in the DI container.
/// </summary>
public static class EndpointExtensions
{
    /// <summary>
    ///     Discovers concrete endpoint types in the supplied or executing assembly and registers them in
    ///     mapping order.
    /// </summary>
    /// <param name="services">The IServiceCollection to add the endpoints to.</param>
    /// <param name="assembly">The assembly to scan. Defaults to the executing assembly.</param>
    /// <returns>The IServiceCollection so that additional calls can be chained.</returns>
    /// <exception cref="System.Reflection.ReflectionTypeLoadException">Types in the supplied assembly cannot be loaded.</exception>
    public static IServiceCollection AddEndpoints(this IServiceCollection services, Assembly? assembly = null)
    {
        assembly ??= Assembly.GetExecutingAssembly();

        var endpointTypes = assembly
            .GetTypes()
            .Where(t => t is { IsClass: true, IsAbstract: false } && typeof(IEndpoint).IsAssignableFrom(t))
            .OrderBy(GetEndpointRegistrationOrder)
            .ThenBy(t => t.FullName, StringComparer.Ordinal);

        foreach (var type in endpointTypes)
            services.TryAddEnumerable(ServiceDescriptor.Transient(typeof(IEndpoint), type));

        return services;
    }


    /// <summary>
    ///     Orders static assets before ordinary endpoints and the Razor fallback.
    /// </summary>
    /// <param name="endpointType">Discovered endpoint implementation type.</param>
    /// <returns>Zero for static assets, 200 for Razor fallback, or 100 for other endpoints.</returns>
    private static int GetEndpointRegistrationOrder(Type endpointType)
    {
        return endpointType.Name switch
        {
            // Static assets must be mapped before the Razor component fallback endpoint.
            "StaticAssetsEndpoint" => 0,
            "RazorComponentsEndpoint" => 200,
            _ => 100
        };
    }
}