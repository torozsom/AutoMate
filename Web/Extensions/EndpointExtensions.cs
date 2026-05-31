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
    ///     Scans the specified assembly (or the calling assembly) via Reflection, finds all concrete
    ///     classes implementing <see cref="IEndpoint" />, and registers them automatically.
    /// </summary>
    /// <param name="services">The IServiceCollection to add the endpoints to.</param>
    /// <param name="assembly">The assembly to scan. Defaults to the executing assembly.</param>
    /// <returns>The IServiceCollection so that additional calls can be chained.</returns>
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


    private static int GetEndpointRegistrationOrder(Type endpointType)
    {
        return endpointType.Name switch
        {
            "StaticAssetsEndpoint" => 0,
            "RazorComponentsEndpoint" => 200,
            _ => 100
        };
    }
}
