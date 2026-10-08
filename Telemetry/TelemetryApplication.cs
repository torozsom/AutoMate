using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Application.Abstractions.Diagnostics;
using Infrastructure.Data;
using Infrastructure.Diagnostics;
using Infrastructure.Observability;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Telemetry;

/// <summary>Builds the private ingestion host with shared safe logging and its existing authenticated disk-gateway routes.</summary>
public static class TelemetryApplication
{
    /// <summary>Creates the production application; optional host customization runs after service registration before build.</summary>
    public static WebApplication Build(string[] args, Action<WebApplicationBuilder>? configure = null)
    {
        var builder = WebApplication.CreateBuilder(args);

        builder.WebHost.ConfigureKestrel(k => k.Limits.MaxRequestBodySize = 1024 * 1024);
        builder.Services.AddDataProtection();

        builder.Services.AddDbContext<AutoMateDbContext>(o => o
            .UseNpgsql(builder.Configuration.GetConnectionString("DefaultConnection"))
            .UseSnakeCaseNamingConvention());

        builder.Services.AddOptions<TelemetryStorageOptions>().BindConfiguration("TelemetryStorage")
            .Validate(o => o.IsValid() && o.DiskGateway, "The telemetry service requires valid DiskGateway settings.")
            .ValidateOnStart();

        builder.Services.AddOptions<DiskSpoolOptions>().BindConfiguration("DiskSpool").ValidateOnStart();
        builder.Services.AddSingleton<DiskTelemetrySpool>();
        builder.Services.AddSingleton<IDeploymentArchive, DiskDeploymentArchive>();
        builder.Services.AddSingleton<TelemetryAdmissionPolicy>();
        builder.Services.AddSingleton<TelemetryProjectPolicyCache>();
        builder.Services.AddHostedService(s => s.GetRequiredService<DiskTelemetrySpool>());
        builder.Services.AddHostedService<DiskTelemetryDeliveryWorker>();
        builder.Services.AddHostedService<TelemetryDailyAggregationWorker>();
        builder.Services.AddHostedService<DeploymentArchiveCleanupWorker>();
        builder.Services.AddHostedService<DeploymentArchiveBackfillWorker>();

        builder.Services.AddHttpClient("DeploymentTelemetry", c => c.Timeout = TimeSpan.FromSeconds(15))
            .ConfigurePrimaryHttpMessageHandler(sp =>
            {
                var handler = new SocketsHttpHandler { AllowAutoRedirect = false };
                var path = sp.GetRequiredService<IOptions<TelemetryStorageOptions>>().Value.CaCertificatePath;
                if (!string.IsNullOrEmpty(path))
                {
                    var policy = new X509ChainPolicy
                    {
                        TrustMode = X509ChainTrustMode.CustomRootTrust,
                        RevocationMode = X509RevocationMode.NoCheck
                    };
                    policy.CustomTrustStore.Add(X509CertificateLoader.LoadCertificateFromFile(path));
                    handler.SslOptions.CertificateChainPolicy = policy;
                }

                return handler;
            });

        builder.Services.AddSingleton<TelemetryHttpTransport>();
        builder.Services.AddSingleton<IDiagnosticRedactor, DiagnosticRedactor>();
        builder.Services.AddScoped<IDeploymentLogWriter, LokiDeploymentLogs>();
        builder.Services.AddScoped<IDeploymentLogQuery, LokiDeploymentLogs>();
        builder.Services.AddScoped<IDeploymentMetricWriter, MimirDeploymentMetrics>();
        builder.Services.AddScoped<IDeploymentMetricQuery, MimirDeploymentMetrics>();
        builder.Services.AddScoped<IDailyDeploymentMetricQuery, MimirDeploymentMetrics>();
        builder.Services.AddScoped<IDeploymentErrorCountQuery, LokiDeploymentLogs>();

        builder.Services.AddSafePlatformLogging();

        configure?.Invoke(builder);

        var app = builder.Build();

        // This private service credential authenticates AutoMate servers, never customer browsers.
        app.Use(async (context, next) =>
        {
            var expected = context.RequestServices.GetRequiredService<IOptions<TelemetryStorageOptions>>().Value
                .GatewayToken;
            var supplied = context.Request.Headers.Authorization.ToString();
            if (!CryptographicOperations.FixedTimeEquals(SHA256.HashData(Encoding.UTF8.GetBytes(supplied)),
                    SHA256.HashData(Encoding.UTF8.GetBytes("Bearer " + expected))))
            {
                context.Response.StatusCode = 401;
                return;
            }

            try
            {
                await next(context);
            }
            catch (TelemetryProviderException ex)
            {
                context.Response.StatusCode = ex.Status;
            }
        });

        app.MapGet("/status",
            async (DiskTelemetrySpool spool, CancellationToken token) => Results.Ok(await spool.StatusAsync(token)));

        app.MapPost("/ingest", async (TelemetryIngestRequest request, TelemetryAdmissionPolicy admission,
            DiskTelemetrySpool spool,
            IDiagnosticRedactor redactor, IOptions<TelemetryStorageOptions> options, CancellationToken token) =>
        {
            var e = request.Event;
            if (e is null || e.ProjectId == Guid.Empty || e.EventId is null || e.EventId == Guid.Empty ||
                e.TerminalChannel is null ||
                e.Message is null || e.Message.Length > 8192 || e.TerminalChannel.Target?.Length > 128 ||
                request.Channel?.Length > 128 || !Enum.IsDefined(e.TerminalChannel.Kind) ||
                !Enum.IsDefined(e.Source) || !Enum.IsDefined(e.Severity) || !Enum.IsDefined(e.Kind) ||
                e.TimestampUtc > DateTimeOffset.UtcNow.AddMinutes(5) ||
                e.TimestampUtc < DateTimeOffset.UtcNow.AddDays(-30) ||
                (e.Metrics is { } samples && (samples.Count > 3 || samples.Any(s => !double.IsFinite(s.Value) ||
                    s.Value < 0 ||
                    !MimirDeploymentMetrics.SupportedUnits
                        .TryGetValue(s.Name, out var unit) ||
                    unit != s.Unit))))
                return Results.BadRequest();
            var project = await admission.GetAsync(e.ProjectId, e.DeploymentId, token);
            if (project is null || (options.Value.ManagedService && !project.ManagedTelemetryConsent))
                return Results.StatusCode(403);
            var receipt = await spool.AppendAsync(project.UserId, redactor.Redact(e).Event,
                request.Channel is null ? null : redactor.RedactText(request.Channel, 128), token);
            return Results.Ok(receipt);
        });

        app.MapGet("/pending/{tenant:guid}/{project:guid}/{deployment:guid}", async (Guid tenant, Guid project,
            Guid deployment,
            AutoMateDbContext db, DiskTelemetrySpool spool, CancellationToken token) =>
        {
            if (!await db.Deployments.AnyAsync(d => d.Id == deployment && d.CsProject!.AppId == project &&
                                                    d.CsProject.Application.UserId == tenant, token))
                return Results.StatusCode(403);
            return Results.Ok(await spool.PendingAsync(tenant, project, deployment, token));
        });

        app.MapGet("/archive/{tenant:guid}/{project:guid}/{deployment:guid}", async (Guid tenant, Guid project,
            Guid deployment, long? cursor, bool? backwards, int? limit, string? search,
            AutoMateDbContext db, IDeploymentArchive archive, CancellationToken token) =>
        {
            if (!await db.Deployments.AnyAsync(d => d.Id == deployment && d.CsProject!.AppId == project &&
                                                    d.CsProject.Application.UserId == tenant &&
                                                    (!builder.Configuration.GetValue<bool>(
                                                         "TelemetryStorage:ManagedService") ||
                                                     d.CsProject.Application.ManagedTelemetryConsent), token))
                return Results.StatusCode(403);
            return Results.Ok(await archive.ReadAsync(tenant, project, deployment, cursor ?? 0, backwards ?? true,
                limit ?? 500, search, token));
        });
        app.MapPost("/archive/metrics", async (ArchiveMetricRequest request, AutoMateDbContext db,
            IDeploymentArchive archive, CancellationToken token) =>
        {
            if (!await db.Deployments.AnyAsync(d =>
                    d.Id == request.Deployment && d.CsProject!.AppId == request.Project &&
                    d.CsProject.Application.UserId == request.Tenant &&
                    (!builder.Configuration.GetValue<bool>("TelemetryStorage:ManagedService") ||
                     d.CsProject.Application.ManagedTelemetryConsent), token)) return Results.StatusCode(403);
            if (request.End <= request.Start || request.MaximumPoints is < 1 or > 1000) return Results.BadRequest();
            return Results.Ok(await archive.ReadMetricsAsync(request.Tenant, request.Project, request.Deployment,
                request.Start, request.End, request.MaximumPoints, token));
        });
        // Import uses a separate route: historical timestamps are valid here, while ordinary ingestion stays bounded.
        app.MapPost("/archive/import", async (DeploymentLogEnvelope request, AutoMateDbContext db,
            IDeploymentArchive archive, CancellationToken token) =>
        {
            var e = request.Event;
            if (e is null || e.Message is null || e.TerminalChannel is null ||
                request.EventId == Guid.Empty || request.OrderId <= 0 || e.Message.Length > 8192 ||
                request.Channel?.Length > 128 || e.TerminalChannel.Target?.Length > 128 ||
                !Enum.IsDefined(e.Source) || !Enum.IsDefined(e.Kind) || !Enum.IsDefined(e.Severity) ||
                !Enum.IsDefined(e.TerminalChannel.Kind) || e.TimestampUtc > DateTimeOffset.UtcNow.AddMinutes(5) ||
                (e.Metrics is { } samples && (samples.Count > 3 || samples.Any(m => !double.IsFinite(m.Value) ||
                    m.Value < 0 || !MimirDeploymentMetrics.SupportedUnits.TryGetValue(m.Name, out var unit) ||
                    unit != m.Unit))))
                return Results.BadRequest();
            if (!await db.Deployments.AnyAsync(d =>
                    d.Id == request.Event.DeploymentId && d.CsProject!.AppId == request.Event.ProjectId &&
                    d.CsProject.Application.UserId == request.TenantId &&
                    (!builder.Configuration.GetValue<bool>("TelemetryStorage:ManagedService") ||
                     d.CsProject.Application.ManagedTelemetryConsent), token)) return Results.StatusCode(403);
            if (request.EventId == Guid.Empty || request.OrderId <= 0 || request.Event.Message.Length > 8192 ||
                request.Event.TimestampUtc > DateTimeOffset.UtcNow.AddMinutes(5)) return Results.BadRequest();
            return Results.Ok(await archive.AppendAsync(request, token));
        });
        app.MapPost("/archive/import-metrics", async (ArchiveMetricImport request, AutoMateDbContext db,
            IDeploymentArchive archive, CancellationToken token) =>
        {
            if (!await db.Deployments.AnyAsync(d =>
                    d.Id == request.Deployment && d.CsProject!.AppId == request.Project &&
                    d.CsProject.Application.UserId == request.Tenant &&
                    (!builder.Configuration.GetValue<bool>("TelemetryStorage:ManagedService") ||
                     d.CsProject.Application.ManagedTelemetryConsent), token))
                return Results.StatusCode(403);
            if (request.Points is null || request.Points.Count > 3000) return Results.BadRequest();
            await archive.ImportMetricsAsync(request, token);
            return Results.Ok(new { });
        });
        app.MapPost("/archive/delete", async (ArchiveDeleteRequest request, AutoMateDbContext db,
            IDeploymentArchive archive, CancellationToken token) =>
        {
            if (await db.Applications.AnyAsync(p => p.Id == request.Project, token) ||
                !await db.DeploymentArchiveCleanups.AnyAsync(
                    p => p.ProjectId == request.Project && p.TenantId == request.Tenant, token))
                return Results.StatusCode(403);
            await archive.DeleteProjectAsync(request.Tenant, request.Project, token);
            return Results.Ok(new { });
        });
        return app;
    }
}