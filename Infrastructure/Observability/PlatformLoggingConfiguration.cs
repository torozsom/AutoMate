using Application.Abstractions.Diagnostics.Redaction;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using DiagnosticRedactor = Infrastructure.Diagnostics.Redaction.DiagnosticRedactor;

namespace Infrastructure.Observability;

/// <summary>
///     Installs the shared pre-provider logging boundary in Web and private Telemetry hosts.
/// </summary>
public static class PlatformLoggingConfiguration
{
    /// <summary>
    ///     Wraps the host factory once, sanitizing all registered providers without changing
    ///     filters or ownership.
    /// </summary>
    /// <param name="services">Provides the service collection boundary used by this operation.</param>
    /// <returns>The same collection with shared platform safety services registered.</returns>
    public static IServiceCollection AddSafePlatformLogging(this IServiceCollection services)
    {
        if (services.Any(service => service.ServiceType == typeof(RegistrationMarker))) return services;
        services.AddLogging();
        services.TryAddSingleton<OperationalWarningLimiter>();
        services.Configure<LoggerFilterOptions>(filters =>
        {
            foreach (var category in new[]
                     {
                         "System.Net.Http.HttpClient", "Microsoft.AspNetCore.Hosting.Diagnostics",
                         "Microsoft.AspNetCore.Routing.EndpointMiddleware"
                     })
                if (filters.Rules.All(rule => rule.CategoryName != category))
                    filters.Rules.Insert(0, new LoggerFilterRule(null, category, LogLevel.Warning, null));
        });
        services.TryAddSingleton<IDiagnosticRedactor, DiagnosticRedactor>();
        services.TryAddSingleton<PlatformTelemetryPolicy>();
        services.TryAddSingleton(provider => new ConsoleExceptionDiagnostics(
            provider.GetRequiredService<IDiagnosticRedactor>(), provider.GetService<IConfiguration>(),
            filters: provider.GetService<IOptionsMonitor<LoggerFilterOptions>>()));
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
            return new SafeLoggerFactory(original, provider.GetRequiredService<PlatformTelemetryPolicy>(),
                provider.GetRequiredService<ConsoleExceptionDiagnostics>());
        }));
        return services;
    }

    /// <summary>
    ///     Marks that this collection has had its logging factory wrapped.
    /// </summary>
    private sealed class RegistrationMarker;
}
