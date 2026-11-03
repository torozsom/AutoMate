using Application.Abstractions.Diagnostics.Redaction;
using Application.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OpenTelemetry;
using OpenTelemetry.Exporter;
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;

namespace Infrastructure.Observability.OpenTelemetry;

/// <summary>
///     Registers the same safe, bounded export pipeline for both application hosts.
/// </summary>
public static class SafeOpenTelemetryRegistration
{
    /// <summary>
    ///     Adds process-wide resources and sanitized asynchronous exporters without starting external services.
    /// </summary>
    /// <param name="builder">Host builder owning the providers.</param>
    /// <param name="serviceName">Default reviewed host identity, overridden by operator configuration.</param>
    /// <param name="includeWebSources">Whether UI and security activity sources are collected.</param>
    public static void AddSafeOpenTelemetry(this IHostApplicationBuilder builder, string serviceName,
        bool includeWebSources = false)
    {
        if (builder.Services.Any(s => s.ServiceType == typeof(RegistrationMarker))) return;
        builder.Services.AddSingleton(new RegistrationMarker());
        builder.Services.AddSafePlatformLogging();
        builder.Services.TryAddEnumerable(ServiceDescriptor.Singleton<IValidateOptions<OpenTelemetryOptions>,
            OpenTelemetryOptionsValidator>());
        builder.Services.AddOptions<OpenTelemetryOptions>().Configure(o => o.ServiceName = serviceName)
            .Bind(builder.Configuration.GetSection(OpenTelemetryOptions.SectionName)).ValidateOnStart();
        var settings = new OpenTelemetryOptions { ServiceName = serviceName };
        builder.Configuration.GetSection(OpenTelemetryOptions.SectionName).Bind(settings);
        var explicitEndpoint =
            OpenTelemetryOptionsValidator.TryGetCollectorEndpoint(settings.OtlpEndpoint, out var endpoint);
        var export = explicitEndpoint ||
                     !string.IsNullOrWhiteSpace(builder.Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"]);
        var version = typeof(SafeOpenTelemetryRegistration).Assembly.GetName().Version?.ToString() ?? "unknown";
        if (explicitEndpoint)
            foreach (var signal in new[] { "logs", "traces", "metrics" })
                builder.Services.PostConfigure<OtlpExporterOptions>("platform-" + signal, options =>
                    options.Endpoint = options.Protocol == OtlpExportProtocol.HttpProtobuf
                        ? new Uri(endpoint!.AbsoluteUri.TrimEnd('/') + "/v1/" + signal)
                        : endpoint!);

        builder.Logging.AddOpenTelemetry(logging =>
        {
            logging.IncludeFormattedMessage = true;
            logging.IncludeScopes = true;
            logging.AddProcessor(sp => new SafeLogProcessor(sp.GetRequiredService<PlatformTelemetryPolicy>()));
            if (settings.ExportConsole) logging.AddConsoleExporter();
            if (export)
                logging.AddOtlpExporter("platform-logs", (_, processor) =>
                {
                    processor.ExportProcessorType = ExportProcessorType.Batch;
                    processor.BatchExportProcessorOptions.MaxQueueSize = 2048;
                    processor.BatchExportProcessorOptions.MaxExportBatchSize = 512;
                    processor.BatchExportProcessorOptions.ExporterTimeoutMilliseconds = 5000;
                });
        });

        builder.Services.AddOpenTelemetry().ConfigureResource(resource => resource.Clear().AddDetector(sp =>
                new SafeResourceDetector(sp.GetRequiredService<IDiagnosticRedactor>(), settings.ServiceName,
                    settings.Environment ?? builder.Environment.EnvironmentName, version,
                    string.Equals(builder.Configuration["HostingProfile:Mode"], "SaaS",
                        StringComparison.OrdinalIgnoreCase)
                        ? "SaaS"
                        : "SelfHosted")))
            .WithTracing(tracing =>
            {
                tracing.AddProcessor(sp => new SafeTraceProcessor(sp.GetRequiredService<PlatformTelemetryPolicy>()));
                tracing.AddProcessor(new OperationalTraceFilter(settings));
                tracing.AddAspNetCoreInstrumentation().AddHttpClientInstrumentation()
                    .AddEntityFrameworkCoreInstrumentation().AddSource(AutoMateTelemetry.Deployments.Name)
                    .AddSource(AnalysisTelemetry.Source.Name);
                if (includeWebSources)
                    tracing.AddSource(AutoMateTelemetry.Security.Name, "Microsoft.AspNetCore.SignalR.Server");
                if (settings.ExportConsole) tracing.AddConsoleExporter();
                if (export)
                    tracing.AddOtlpExporter("platform-traces", options =>
                    {
                        options.ExportProcessorType = ExportProcessorType.Batch;
                        options.BatchExportProcessorOptions.MaxQueueSize = 2048;
                        options.BatchExportProcessorOptions.MaxExportBatchSize = 512;
                        options.BatchExportProcessorOptions.ExporterTimeoutMilliseconds = 5000;
                    });
            })
            .WithMetrics(metrics =>
            {
                SafeMetricPolicy.Configure(metrics);
                metrics.AddAspNetCoreInstrumentation().AddHttpClientInstrumentation().AddRuntimeInstrumentation()
                    .AddMeter(AutoMateTelemetry.DeploymentMeter.Name, AutoMateTelemetry.SecurityMeter.Name,
                        AnalysisTelemetry.Meter.Name, "AutoMate.TelemetryStorage", OperationalMetrics.MeterName);
                if (settings.ExportConsole) metrics.AddConsoleExporter();
                if (export) metrics.AddOtlpExporter("platform-metrics", _ => { });
            });
        // OTLP SDK exporters suppress their own HttpClient instrumentation; never attach application handlers to them.
    }

    /// <summary>
    ///     Marks a host whose providers have already been configured.
    /// </summary>
    private sealed class RegistrationMarker;
}
