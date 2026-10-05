using Application.Abstractions.Diagnostics;
using Infrastructure.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace Infrastructure.Observability;

/// <summary>Installs the shared pre-provider logging boundary in Web and private Telemetry hosts.</summary>
public static class PlatformLoggingConfiguration
{
    /// <summary>Wraps the host factory once, sanitizing all registered providers without changing filters or ownership.</summary>
    public static IServiceCollection AddSafePlatformLogging(this IServiceCollection services)
    {
        if (services.Any(service => service.ServiceType == typeof(RegistrationMarker))) return services;
        services.AddLogging();
        services.TryAddSingleton<IDiagnosticRedactor, DiagnosticRedactor>();
        services.TryAddSingleton<PlatformTelemetryPolicy>();
        services.AddSingleton(new RegistrationMarker());
        services.Configure<LoggerFactoryOptions>(settings => settings.ActivityTrackingOptions =
            ActivityTrackingOptions.TraceId | ActivityTrackingOptions.SpanId | ActivityTrackingOptions.ParentId);
        var originalFactory = services.Last(service => service.ServiceType == typeof(ILoggerFactory));
        services.Replace(ServiceDescriptor.Singleton<ILoggerFactory>(provider =>
        {
            var original = originalFactory.ImplementationInstance as ILoggerFactory ??
                           originalFactory.ImplementationFactory?.Invoke(provider) as ILoggerFactory ??
                           (ILoggerFactory)ActivatorUtilities.CreateInstance(provider,
                               originalFactory.ImplementationType!);
            return new SafeLoggerFactory(original, provider.GetRequiredService<PlatformTelemetryPolicy>());
        }));
        return services;
    }

    /// <summary>Marks that this collection has had its logging factory wrapped.</summary>
    private sealed class RegistrationMarker;
}