using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Application.Abstractions.Ai;
using Application.Abstractions.Diagnostics;
using Infrastructure.Data;
using Infrastructure.Diagnostics;
using Infrastructure.Observability;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Telemetry;

/// <summary>
///     Composes the private telemetry host, safe platform logging and authenticated ingestion/archive routes.
/// </summary>
public static class TelemetryApplication
{
    /// <summary>
    ///     Registers telemetry dependencies and maps the private HTTP pipeline without starting the host.
    /// </summary>
    /// <param name="args">Command-line arguments used by the application builder to configure the host.</param>
    /// <param name="configure">
    ///     Optional builder customization invoked after production service registration and before host construction.
    /// </param>
    /// <returns>The configured, unstarted application; the caller owns its startup, shutdown and disposal.</returns>
    /// <exception cref="InvalidOperationException">
    ///     Builder customization has already built the host, preventing a second build.
    /// </exception>
    /// <remarks>
    ///     Customization and host-construction failures propagate to the caller. Startup validates options and acquires
    ///     the exclusive spool-volume lock; only one active host may own that volume. Apply database migrations separately.
    /// </remarks>
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
        builder.Services.AddScoped<ArchiveMetricBatchReader>();
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

        /*
         * Authenticate every route with the private server credential using fixed-time hash comparison.
         * Missing or incorrect credentials return 401; downstream telemetry-provider failures retain only their status.
         */
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

        /* Return bounded spool status; the request token cancels the asynchronous status read. */
        app.MapGet("/status",
            async (DiskTelemetrySpool spool, CancellationToken token) => Results.Ok(await spool.StatusAsync(token)));

        /* Publish the archive protocol and operation catalog without tenant data or credentials. */
        app.MapGet("/capabilities", () => Results.Ok(new TelemetryCapabilities(
            TelemetryCapabilities.CurrentVersion, TelemetryCapabilities.RequiredArchiveOperations)));

        /*
         * Reject malformed, oversized or unsupported observations with 400 and denied admission with 403.
         * Resolve the owner through admission policy, redact again and return the durable spool receipt.
         */
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
            if (project is null)
                return Results.StatusCode(403);
            var receipt = await spool.AppendAsync(project.UserId, redactor.Redact(e).Event,
                request.Channel is null ? null : redactor.RedactText(request.Channel, 128), token);
            return Results.Ok(receipt);
        });

        /* Verify the tenant/project/deployment relationship before returning bounded pending output. */
        app.MapGet("/pending/{tenant:guid}/{project:guid}/{deployment:guid}", async (Guid tenant, Guid project,
            Guid deployment,
            AutoMateDbContext db, DiskTelemetrySpool spool, CancellationToken token) =>
        {
            if (!await db.Deployments.AnyAsync(d => d.Id == deployment && d.CsProject!.AppId == project &&
                                                    d.CsProject.Application.UserId == tenant, token))
                return Results.StatusCode(403);
            return Results.Ok(await spool.PendingAsync(tenant, project, deployment, token));
        });

        /* Read the owned immutable log-context selection; invalid archive arguments become 400 responses. */
        app.MapPost("/archive/assessment", async (ArchiveAssessmentQuery request, AutoMateDbContext db,
            IDeploymentArchive archive, CancellationToken token) =>
        {
            if (!await db.Deployments.AnyAsync(d =>
                    d.Id == request.Deployment && d.CsProject!.AppId == request.Project &&
                    d.CsProject.Application.UserId == request.Tenant, token)) return Results.StatusCode(403);
            try
            {
                return Results.Ok(await archive.ReadAssessmentAsync(request, token));
            }
            catch (ArgumentException)
            {
                return Results.BadRequest();
            }
        });
        /* Read selected metric evidence only for an owned deployment and a positive window of at most 365 days. */
        app.MapPost("/archive/assessment-metrics", async (ArchiveAssessmentQuery request, AutoMateDbContext db,
            IDeploymentArchive archive, CancellationToken token) =>
        {
            if (!await db.Deployments.AnyAsync(d =>
                    d.Id == request.Deployment && d.CsProject!.AppId == request.Project &&
                    d.CsProject.Application.UserId == request.Tenant, token)) return Results.StatusCode(403);
            if (request.Window.End <= request.Window.Start ||
                request.Window.End - request.Window.Start > TimeSpan.FromDays(365))
                return Results.BadRequest();
            try
            {
                return Results.Ok(await archive.ReadAssessmentMetricsAsync(request, token));
            }
            catch (ArgumentException)
            {
                return Results.BadRequest();
            }
        });

        /* Read owned log history, defaulting to the latest backward page of 500 records when options are absent. */
        app.MapGet("/archive/{tenant:guid}/{project:guid}/{deployment:guid}", async (Guid tenant, Guid project,
            Guid deployment, long? cursor, bool? backwards, int? limit, string? search,
            AutoMateDbContext db, IDeploymentArchive archive, CancellationToken token) =>
        {
            if (!await db.Deployments.AnyAsync(d => d.Id == deployment && d.CsProject!.AppId == project &&
                                                    d.CsProject.Application.UserId == tenant, token))
                return Results.StatusCode(403);
            return Results.Ok(await archive.ReadAsync(tenant, project, deployment, cursor ?? 0, backwards ?? true,
                limit ?? 500, search, token));
        });
        /* Delegate bounded partition reads; denied ownership becomes 403 and invalid request arguments become 400. */
        app.MapPost("/archive/metric-batch", async (ArchiveMetricBatchRequest request, ArchiveMetricBatchReader reader,
            CancellationToken token) =>
        {
            try
            {
                return Results.Ok(await reader.ReadAsync(request, token));
            }
            catch (UnauthorizedAccessException)
            {
                return Results.StatusCode(403);
            }
            catch (ArgumentException)
            {
                return Results.BadRequest();
            }
        });
        /* Read owned metric history after checking positive bounds and a requested point limit from 1 through 1,000. */
        app.MapPost("/archive/metrics", async (ArchiveMetricRequest request, AutoMateDbContext db,
            IDeploymentArchive archive, CancellationToken token) =>
        {
            if (!await db.Deployments.AnyAsync(d =>
                    d.Id == request.Deployment && d.CsProject!.AppId == request.Project &&
                    d.CsProject.Application.UserId == request.Tenant, token)) return Results.StatusCode(403);
            if (request.End <= request.Start || request.MaximumPoints is < 1 or > 1000) return Results.BadRequest();
            return Results.Ok(await archive.ReadMetricsAsync(request.Tenant, request.Project, request.Deployment,
                request.Start, request.End, request.MaximumPoints, token));
        });
        /*
         * Import validated historical envelopes separately from ordinary ingestion's 30-day timestamp boundary.
         * Preserve positive ordering and event identity, verify ownership, and return the archive acknowledgement.
         */
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
                    d.CsProject.Application.UserId == request.TenantId, token)) return Results.StatusCode(403);
            if (request.EventId == Guid.Empty || request.OrderId <= 0 || request.Event.Message.Length > 8192 ||
                request.Event.TimestampUtc > DateTimeOffset.UtcNow.AddMinutes(5)) return Results.BadRequest();
            return Results.Ok(await archive.AppendAsync(request, token));
        });
        /* Import at most 3,000 points for an owned deployment; acknowledge only after the archive operation completes. */
        app.MapPost("/archive/import-metrics", async (ArchiveMetricImport request, AutoMateDbContext db,
            IDeploymentArchive archive, CancellationToken token) =>
        {
            if (!await db.Deployments.AnyAsync(d =>
                    d.Id == request.Deployment && d.CsProject!.AppId == request.Project &&
                    d.CsProject.Application.UserId == request.Tenant, token))
                return Results.StatusCode(403);
            if (request.Points is null || request.Points.Count > 3000) return Results.BadRequest();
            await archive.ImportMetricsAsync(request, token);
            return Results.Ok(new { });
        });
        /* Delete retained history only after project removal and an owner-matching durable cleanup request. */
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
