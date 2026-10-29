using System.Diagnostics;
using System.Security.Claims;
using System.Security.Cryptography.X509Certificates;
using System.Threading.RateLimiting;
using Application.Abstractions.Ai.Context;
using Application.Abstractions.Ai.Execution;
using Application.Abstractions.Ai.Policy;
using Application.Abstractions.Ai.Results;
using Application.Abstractions.Azure;
using Application.Abstractions.Diagnostics.Delivery;
using Application.Abstractions.Diagnostics.Events;
using Application.Abstractions.Diagnostics.History;
using Application.Abstractions.Diagnostics.Metrics;
using Application.Abstractions.Diagnostics.Redaction;
using Application.Abstractions.Docker;
using Application.Abstractions.Email;
using Application.Abstractions.GitHub;
using Application.Abstractions.Hosting;
using Application.Abstractions.Logging;
using Application.Abstractions.Scanning;
using Application.Abstractions.Templating;
using Application.Ai.Policy;
using Application.Ai.Results;
using Application.Auth;
using Application.Data.Apps;
using Application.Data.Users;
using Application.Diagnostics;
using Application.Orchestration.Cloud;
using Application.Orchestration.Jobs;
using Application.Orchestration.Local;
using Application.Orchestration.Status;
using Domain.Entities.Users;
using Infrastructure.Ai.Admission;
using Infrastructure.Ai.Context;
using Infrastructure.Ai.Processing;
using Infrastructure.Ai.Providers;
using Infrastructure.ApplicationServices.Auth;
using Infrastructure.ApplicationServices.Data.Apps;
using Infrastructure.ApplicationServices.Data.Users;
using Infrastructure.ApplicationServices.Orchestration.CloudPreparation;
using Infrastructure.ApplicationServices.Orchestration.CloudRuns;
using Infrastructure.ApplicationServices.Orchestration.Lifecycle;
using Infrastructure.ApplicationServices.Orchestration.Local;
using Infrastructure.Azure.ContainerApps;
using Infrastructure.Azure.Identity;
using Infrastructure.Azure.Monitor;
using Infrastructure.Azure.Provisioning;
using Infrastructure.Data;
using Infrastructure.Diagnostics.Archive;
using Infrastructure.Diagnostics.Delivery;
using Infrastructure.Diagnostics.Metrics;
using Infrastructure.Diagnostics.Policy;
using Infrastructure.Diagnostics.Redaction;
using Infrastructure.Diagnostics.Runtime;
using Infrastructure.Diagnostics.Storage;
using Infrastructure.Docker.Runtime;
using Infrastructure.Email;
using Infrastructure.GitHub.Api;
using Infrastructure.GitHub.Authentication;
using Infrastructure.GitHub.Webhooks;
using Infrastructure.Observability;
using Infrastructure.Scanner.Filesystem;
using Infrastructure.Scanner.Projects;
using Infrastructure.Templating.Rendering;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;
using Web.Configs.Observability;
using Web.Extensions;
using Web.Observability;
using Web.Services;

namespace Web.Configs.Hosting;

/// <summary>
///     Provides a centralized, modular configuration for ASP.NET Core dependency injection. Ensures
///     separation of concerns by splitting infrastructure, security, and presentation setups.
/// </summary>
public static class ServiceConfiguration
{
    /// <summary>Configuration key for the application database connection.</summary>
    private const string DefaultConnectionKey = "DefaultConnection";

    /// <summary>Configuration key for optional distributed caching.</summary>
    private const string RedisConnectionKey = "Redis";

    /// <summary>Fixed application identity used by service composition.</summary>
    private const string AppName = "AutoMate";


    /// <summary>Log Analytics delegated token scope.</summary>
    private const string AzureMonitorLogsScope = "https://api.loganalytics.io/.default";


    /// <summary>
    ///     Extension method to configure application services.
    /// </summary>
    /// <param name="builder">The WebApplicationBuilder used to configure services.</param>
    extension(WebApplicationBuilder builder)
    {
        /// <summary>
        ///     Bootstraps all application dependencies.
        /// </summary>
        /// <returns>The original WebApplicationBuilder for chaining.</returns>
        public WebApplicationBuilder AddApplicationServices()
        {
            // Add Logging Services
            builder.Services.AddLogging();

            // Add Health Checks
            builder.Services.AddHealthChecks()
                .AddCheck<AnalysisReadinessHealthCheck>("ai_analysis", tags: [AnalysisHealthChecks.ReadinessTag],
                    timeout: TimeSpan.FromSeconds(5));

            // Bind Strongly-Typed Configurations
            var deploymentCapabilities = builder.AddConfigurations();

            // Add operational telemetry before registering deployment adapters.
            builder.AddObservability();

            // Add Infrastructure (DB, Redis, Clients)
            builder.AddInfrastructure();

            // Add Security (Auth, RateLimiting, DataProtection, Proxies)
            builder.AddSecurity();

            // Add Presentation Layer (Blazor, SignalR, Swagger)
            builder.AddPresentation();

            // Add Business Logic Services
            builder.Services.RegisterDomainServices(deploymentCapabilities);

            // Register Minimal API Endpoints
            builder.Services.AddEndpoints();

            return builder;
        }


        /// <summary>
        ///     Binds application settings to strongly-typed option classes using the Options Pattern.
        /// </summary>
        /// <returns>Server-enforced capability flags for the configured hosting profile.</returns>
        private IDeploymentCapabilities AddConfigurations()
        {
            builder.Services.Configure<EmailOptions>(builder.Configuration.GetSection(EmailOptions.SectionName));
            builder.Services.Configure<DockerOptions>(builder.Configuration.GetSection(DockerOptions.SectionName));

            builder.Services.AddOptions<DeploymentDiagnosticOptions>()
                .Bind(builder.Configuration.GetSection(DeploymentDiagnosticOptions.SectionName))
                .Validate(options => options.BufferCapacity is >= 16 and <= 16_384 &&
                                     options.PersistenceTimeoutSeconds is >= 1 and <= 60 &&
                                     options.DeliveryTimeoutSeconds is >= 1 and <= 30,
                    "DeploymentDiagnostics buffer capacity or sink deadlines are outside their supported ranges.")
                .ValidateOnStart();

            builder.Services.Configure<GitHubWorkflowMonitoringOptions>(
                builder.Configuration.GetSection(GitHubWorkflowMonitoringOptions.SectionName));

            builder.Services.AddOptions<DeploymentConcurrencyOptions>()
                .Bind(builder.Configuration.GetSection(DeploymentConcurrencyOptions.SectionName))
                .Validate(options => options.MaxLocalBuilds is >= -1 and <= 1_024 &&
                                     options.MaxLocalStops is >= 0 and <= 1_024 &&
                                     options.MaxCloudDeployments is >= 1 and <= 16 &&
                                     options.MaxQueuedJobs is >= 1 and <= 1_000,
                    "Deployment concurrency limits must be within their supported ranges.")
                .ValidateOnStart();

            builder.Services.AddOptions<CloudSaasOptions>()
                .Bind(builder.Configuration.GetSection(CloudSaasOptions.SectionName))
                .Validate(options => options.MaxQueuedPerUser is >= 1 and <= 1_000 &&
                                     options.MaxActivePerUser is >= 1 and <= 128 &&
                                     options.MaxActivePerInstallation is >= 1 and <= 128 &&
                                     options.MaxActiveGlobally is >= 1 and <= 4_096 &&
                                     options.MaxActivePerWorker is >= 1 and <= 256 &&
                                     options.SchedulerPollSeconds is >= 1 and <= 60,
                    "Cloud SaaS admission limits must be within their supported ranges.")
                .ValidateOnStart();

            builder.Services.AddOptions<GitHubAppOptions>()
                .Bind(builder.Configuration.GetSection(GitHubAppOptions.SectionName));

            builder.Services.AddSingleton<IValidateOptions<OpenTelemetryOptions>, OpenTelemetryOptionsValidator>();

            builder.Services.AddOptions<OpenTelemetryOptions>()
                .Bind(builder.Configuration.GetSection(OpenTelemetryOptions.SectionName))
                .ValidateOnStart();

            builder.Services.AddOptions<AiAnalysisOptions>()
                .Bind(builder.Configuration.GetSection(AiAnalysisOptions.SectionName))
                .Validate(AnalysisBudgetPolicy.IsValid,
                    "AI tenant, rate, shared concurrency and exact monetary budget limits must be within supported bounds.")
                .Validate<AnalysisProviderCatalog>(
                    (settings, catalog) => !settings.ProviderEgressEnabled || catalog.IsConfigured(settings),
                    "AI egress requires explicit provider, tenant, category and matching regional processing approvals with bounded context and retention.")
                .Validate(settings => settings.TimeoutSeconds is >= 5 and <= 300,
                    "AiAnalysis:TimeoutSeconds must be 5–300 seconds.")
                .Validate(settings => settings.MaximumOutputTokens is >= 1 and <= 8192,
                    "AiAnalysis:MaximumOutputTokens must be 1–8,192 tokens.")
                .Validate(settings => settings.MaximumContextCharacters is >= 1 and <= 131072 &&
                                      settings.MaximumContextBytes is >= 1 and <= 131072 &&
                                      settings.MaximumContextTokens is >= 1 and <= 131072,
                    "AiAnalysis context character, encoded byte and conservative token limits must each be 1–131,072.")
                .Validate(settings => settings.ResultRetentionDays is >= 1 and <= 90,
                    "AiAnalysis:ResultRetentionDays must be 1–90 days.")
                .Validate(
                    settings => settings.LeaseDurationSeconds is >= 30 and <= 900 &&
                                settings.MaximumRecoveryAttempts is >= 1 and <= 10,
                    "AI queue lease lifetime must be 30–900 seconds and recovery attempts must be 1–10.")
                .Validate(settings => settings.MaximumProviderRetries is >= 0 and <= 5 &&
                                      settings.RetryBaseDelaySeconds is >= 1 and <= 300 &&
                                      settings.RetryMaximumDelaySeconds is >= 5 and <= 3600 &&
                                      settings.RetryBaseDelaySeconds <= settings.RetryMaximumDelaySeconds,
                    "AI retries must be 0–5, with a 1–300 second base delay no greater than the 5–3,600 second maximum delay.")
                .Validate(settings => settings.DailyProjectLimit is >= 0 and <= 1000,
                    "AI daily project admission limit must be 0–1,000.")
                .Validate(
                    settings => settings.MaximumConcurrency is >= 1
                        and <= AiAnalysisOptions.MaximumSupportedConcurrency,
                    "AI worker concurrency must be 1–16 per application instance; changes require restart.")
                .ValidateOnStart();

            builder.Services.AddOptions<AzureMonitorLogsOptions>()
                .Bind(builder.Configuration.GetSection(AzureMonitorLogsOptions.SectionName))
                .Validate(settings => settings.DatabaseInitialLookback >= TimeSpan.FromMinutes(1) &&
                                      settings.DatabaseInitialLookback <= TimeSpan.FromDays(7),
                    "Azure database initial log lookback must be between one minute and seven days.")
                .ValidateOnStart();

            builder.Services.Configure<AzureMonitorLogsOptions>(options =>
            {
                var tenant =
                    OAuthAccountCallbacks.GetMicrosoftAuthorityTenant(
                        builder.Configuration["Authentication:Microsoft:TenantId"]);
                options.TokenEndpoint = $"https://login.microsoftonline.com/{tenant}/oauth2/v2.0/token";
                options.ClientId = builder.Configuration["Authentication:Microsoft:ClientId"] ?? string.Empty;
                options.ClientSecret = builder.Configuration["Authentication:Microsoft:ClientSecret"] ?? string.Empty;
                options.Scope = AzureMonitorLogsScope;
            });

            var hostingProfile = builder.Configuration.GetSection(HostingProfileOptions.SectionName)
                .Get<HostingProfileOptions>() ?? new HostingProfileOptions();

            var capabilities = hostingProfile.ToCapabilities();

            if (capabilities is { LocalDeploymentsEnabled: false, CloudDeploymentsEnabled: true })
                builder.Services.AddOptions<GitHubAppOptions>()
                    .Validate(options => options.AppId > 0 &&
                                         !string.IsNullOrWhiteSpace(options.AppSlug) &&
                                         !string.IsNullOrWhiteSpace(options.PrivateKeyPem) &&
                                         !string.IsNullOrWhiteSpace(options.WebhookSecret),
                        "SaaS requires a configured GitHub App ID, slug, signing key, and webhook secret.")
                    .ValidateOnStart();

            builder.Services.AddSingleton(capabilities);
            builder.Services.AddSingleton(capabilities);
            return capabilities;
        }


        /// <summary>
        ///     Registers OpenTelemetry for AutoMate's own requests, providers, and deployment diagnostics.
        /// </summary>
        private void AddObservability()
        {
            var options = builder.Configuration.GetSection(OpenTelemetryOptions.SectionName)
                .Get<OpenTelemetryOptions>() ?? new OpenTelemetryOptions();

            var exportConsole = options.ExportConsole;
            var hasOtlpEndpoint =
                OpenTelemetryOptionsValidator.TryGetCollectorEndpoint(options.OtlpEndpoint, out var otlpEndpoint);
            // Rider supplies the standard endpoint at launch; let the SDK read its transport and credentials.
            var exportOtlp = hasOtlpEndpoint ||
                             !string.IsNullOrWhiteSpace(builder.Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"]);

            var serviceVersion = typeof(ServiceConfiguration).Assembly.GetName().Version?.ToString() ?? "unknown";

            builder.Services.AddSafePlatformLogging();

            builder.Logging.AddOpenTelemetry(logging =>
            {
                logging.IncludeFormattedMessage = true;
                logging.IncludeScopes = true;
                logging.AddProcessor(services =>
                    new SafeLogProcessor(services.GetRequiredService<PlatformTelemetryPolicy>()));

                if (exportConsole) logging.AddConsoleExporter();
                if (exportOtlp)
                    logging.AddOtlpExporter(exporter =>
                    {
                        if (hasOtlpEndpoint) exporter.Endpoint = otlpEndpoint!;
                    });
            });

            builder.Services.AddOpenTelemetry()
                .ConfigureResource(resource => resource.Clear().AddDetector(services => new SafeResourceDetector(
                    services.GetRequiredService<IDiagnosticRedactor>(), options.ServiceName,
                    options.Environment ?? builder.Environment.EnvironmentName, serviceVersion,
                    string.Equals(builder.Configuration["HostingProfile:Mode"]?.Trim(), "SaaS",
                        StringComparison.OrdinalIgnoreCase)
                        ? "SaaS"
                        : "SelfHosted")))
                .WithTracing(tracing =>
                {
                    tracing.AddProcessor(services =>
                        new SafeTraceProcessor(services.GetRequiredService<PlatformTelemetryPolicy>()));
                    tracing.AddAspNetCoreInstrumentation();
                    tracing.AddHttpClientInstrumentation();
                    tracing.AddEntityFrameworkCoreInstrumentation();
                    tracing.AddSource(AutoMateTelemetry.Deployments.Name);
                    tracing.AddSource(AutoMateTelemetry.Security.Name);
                    tracing.AddSource(AnalysisTelemetry.Source.Name);
                    tracing.AddSource("Microsoft.AspNetCore.SignalR.Server");
                    if (exportConsole) tracing.AddConsoleExporter();
                    if (exportOtlp)
                        tracing.AddOtlpExporter(exporter =>
                        {
                            if (hasOtlpEndpoint) exporter.Endpoint = otlpEndpoint!;
                        });
                })
                .WithMetrics(metrics =>
                {
                    SafeMetricPolicy.Configure(metrics);
                    metrics.AddAspNetCoreInstrumentation();
                    metrics.AddHttpClientInstrumentation();
                    metrics.AddRuntimeInstrumentation();
                    metrics.AddMeter(AutoMateTelemetry.DeploymentMeter.Name);
                    metrics.AddMeter(AutoMateTelemetry.SecurityMeter.Name);
                    metrics.AddMeter(AnalysisTelemetry.Meter.Name);
                    metrics.AddMeter("AutoMate.TelemetryStorage");
                    if (exportConsole) metrics.AddConsoleExporter();
                    if (exportOtlp)
                        metrics.AddOtlpExporter(exporter =>
                        {
                            if (hasOtlpEndpoint) exporter.Endpoint = otlpEndpoint!;
                        });
                });
        }


        /// <summary>
        ///     Registers database contexts, caching, and external HTTP clients.
        /// </summary>
        private void AddInfrastructure()
        {
            var services = builder.Services;
            var config = builder.Configuration;

            // PostgreSQL Setup with Connection Pooling
            services.AddDbContextPool<AutoMateDbContext>(options =>
                options.UseNpgsql(config.GetConnectionString(DefaultConnectionKey))
                    .UseSnakeCaseNamingConvention());

            // Redis for Distributed Caching
            services.AddStackExchangeRedisCache(options =>
            {
                options.Configuration = config.GetConnectionString(RedisConnectionKey);
                options.InstanceName = $"{AppName}_";
            });

            // External API Clients with Resilience
            services.AddHttpClient("AzureRuntime", client => client.Timeout = TimeSpan.FromSeconds(15))
                .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { AllowAutoRedirect = false });

            // ResponseHeadersRead bounds connection establishment without timing out a quiet ACA response body.
            services.AddHttpClient("AzureLiveLogs", client => client.Timeout = TimeSpan.FromSeconds(15))
                .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { AllowAutoRedirect = false });

            services.AddHttpClient<IGitHubService, GitHubService>()
                .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { AllowAutoRedirect = false })
                .AddStandardResilienceHandler();

            services.AddHttpClient<IGitHubAppCredentials, GitHubAppCredentials>()
                .AddStandardResilienceHandler();

            services.AddSingleton(new AnalysisProviderRegistration("openai", typeof(OpenAiAnalysisProvider),
                settings => settings.Endpoint == $"https://{settings.ProcessingRegion}.api.openai.com/v1/",
                () => !string.IsNullOrWhiteSpace(config["AiAnalysis:ApiKey"])));

            services.AddSingleton(new AnalysisProviderRegistration(AzureOpenAiAnalysisProvider.ProviderName,
                typeof(AzureOpenAiAnalysisProvider),
                settings => AzureOpenAiAnalysisProvider.ReadOptions(config).ApprovesRoute(settings),
                () => AzureOpenAiAnalysisProvider.ReadOptions(config).HasApiKey()));

            services.AddSingleton<AnalysisProviderCatalog>();
            services.AddScoped<ILlmAnalysisProvider, ConfiguredAnalysisProvider>();

            services.AddHttpClient<OpenAiAnalysisProvider>()
                .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { AllowAutoRedirect = false });

            services.AddHttpClient<AzureOpenAiAnalysisProvider>()
                .RemoveAllLoggers()
                .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { AllowAutoRedirect = false });

            services.AddSingleton<IValidateOptions<TelemetryStorageOptions>, TelemetryStorageOptionsValidator>();

            services.AddOptions<TelemetryStorageOptions>()
                .Configure(o =>
                {
                    o.Backend = "LokiMimir";
                    o.DeliveryMode = "DiskGateway";
                })
                .Bind(config.GetSection(TelemetryStorageOptions.SectionName))
                .Validate(o => o.DiskGateway,
                    "AutoMate requires TelemetryStorage:DeliveryMode=DiskGateway in both SelfHosted and SaaS; database payload fallback is disabled.")
                .ValidateOnStart();

            services.AddHttpClient("DeploymentTelemetry", client => client.Timeout = TimeSpan.FromSeconds(15))
                .ConfigurePrimaryHttpMessageHandler(sp =>
                {
                    var handler = new SocketsHttpHandler { AllowAutoRedirect = false };
                    var settings = sp.GetRequiredService<IOptions<TelemetryStorageOptions>>().Value;
                    if (!string.IsNullOrWhiteSpace(settings.CaCertificatePath))
                    {
                        // Scope private pilot trust to this client; normal hostname and chain verification remain enabled.
                        var policy = new X509ChainPolicy
                        {
                            TrustMode = X509ChainTrustMode.CustomRootTrust,
                            RevocationMode = X509RevocationMode.NoCheck
                        };
                        policy.CustomTrustStore.Add(
                            X509CertificateLoader.LoadCertificateFromFile(settings.CaCertificatePath));
                        handler.SslOptions.CertificateChainPolicy = policy;
                    }

                    return handler;
                });

            services.AddSingleton<TelemetryHttpTransport>();
            services.AddSingleton<TelemetryProjectPolicyCache>();
            services.AddSingleton<TelemetryGatewayCompatibility>();

            services.AddScoped<ITelemetryGateway, TelemetryGatewayClient>();
            services.AddScoped<IDeploymentArchive, TelemetryGatewayClient>();

            services.AddScoped<DeploymentDiagnosticStore>();
            services.AddScoped<DeploymentTelemetryStore>();

            services.AddScoped<IDeploymentDiagnosticStore>(sp => sp.GetRequiredService<DeploymentTelemetryStore>());
            services.AddScoped<LokiDeploymentLogs>();
            services.AddScoped<IDeploymentLogWriter>(sp => sp.GetRequiredService<LokiDeploymentLogs>());
            services.AddScoped<IDeploymentLogQuery>(sp => sp.GetRequiredService<LokiDeploymentLogs>());

            services.AddScoped<MimirDeploymentMetrics>();

            services.AddScoped<IDeploymentMetricWriter>(sp => sp.GetRequiredService<MimirDeploymentMetrics>());
            services.AddScoped<IDeploymentMetricQuery>(sp => sp.GetRequiredService<MimirDeploymentMetrics>());
            services.AddScoped<IDeploymentHistoryService, DeploymentHistoryService>();
            services.AddScoped<IDeploymentDetailsService, DeploymentDetailsService>();
            services.AddScoped<IProjectTelemetryAnalytics, ProjectTelemetryAnalyticsService>();
            services.AddScoped<IMetricExploration, MetricExplorationService>();

            services.AddHostedService<TelemetryDeliveryWorker>();
            services.AddScoped<IAnalysisEgressAuthorizer, AnalysisEgressAuthorizer>();
            services.AddScoped<DeploymentAnalysisService>();
            services.AddScoped<IDeploymentAnalysisService>(provider =>
                provider.GetRequiredService<DeploymentAnalysisService>());

            services.AddScoped<IDeploymentAnalysisContextBuilder, DeploymentAnalysisContextBuilder>();
            services.AddSingleton<IAnalysisResultValidator, AnalysisResultValidator>();
            services.AddScoped<IDeploymentAnalysisQueue, DeploymentAnalysisQueue>();
            services.AddScoped<IAnalysisBudgetGuard, AnalysisBudgetGuard>();
            services.AddScoped<IDeploymentAnalysisReadiness, DeploymentAnalysisReadinessService>();

            services.AddHostedService<DeploymentAnalysisWorker>();
            services.AddHostedService<FailedDeploymentAnalysisDispatcher>();
            services.AddHostedService<DeploymentAnalysisRetentionService>();
        }


        /// <summary>
        ///     Registers authentication, authorization, rate limiting, and security headers.
        /// </summary>
        /// <exception cref="InvalidOperationException">Required host or OAuth configuration is missing.</exception>
        /// <remarks>IMPORTANT: Proxy configuration assumes the application is hosted behind a trusted reverse proxy.</remarks>
        private void AddSecurity()
        {
            var services = builder.Services;
            var config = builder.Configuration;

            // Proxy headers (Must run behind Nginx/Traefik/etc.)
            services.Configure<ForwardedHeadersOptions>(options =>
            {
                options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
                options.KnownIPNetworks.Clear();
                options.KnownProxies.Clear();
            });

            // Add antiforgery services for CSRF protection
            services.AddAntiforgery();

            // Data Protection (Keeps cookies valid across container restarts)
            var dataProtection = services.AddDataProtection()
                .PersistKeysToDbContext<AutoMateDbContext>()
                .SetApplicationName(AppName);

            if (string.Equals(config["HostingProfile:Mode"], "SaaS", StringComparison.OrdinalIgnoreCase))
            {
                var certificatePath = config["SaaS:DataProtectionCertificatePath"];
                var certificatePassword = config["SaaS:DataProtectionCertificatePassword"];

                if (string.IsNullOrWhiteSpace(certificatePath) || !File.Exists(certificatePath))
                    throw new InvalidOperationException(
                        "SaaS requires a mounted Data Protection certificate for encrypted shared keys.");

                dataProtection.ProtectKeysWithCertificate(
                    X509CertificateLoader.LoadPkcs12FromFile(certificatePath, certificatePassword,
                        X509KeyStorageFlags.EphemeralKeySet));
            }

            // Authentication Setup
            services.AddAuthentication(options =>
                {
                    options.DefaultScheme = CookieAuthenticationDefaults.AuthenticationScheme;
                    options.DefaultChallengeScheme = "GitHub";
                })
                .AddCookie(options =>
                {
                    options.Cookie.HttpOnly = true;
                    options.Cookie.SameSite = SameSiteMode.Lax;
                    options.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
                    options.LoginPath = "/login";
                    options.LogoutPath = "/api/auth/logout";
                    options.AccessDeniedPath = "/login";
                })
                .AddGitHub(options =>
                {
                    options.ClientId = config["Authentication:GitHub:ClientId"]
                                       ?? throw new InvalidOperationException(
                                           "Authentication:GitHub:ClientId is missing from configuration.");
                    options.ClientSecret = config["Authentication:GitHub:ClientSecret"]
                                           ?? throw new InvalidOperationException(
                                               "Authentication:GitHub:ClientSecret is missing from configuration.");

                    options.CallbackPath = new PathString("/signin-github");
                    options.Scope.Add("user:email");
                    options.Scope.Add("repo");
                    options.Scope.Add("workflow");
                    options.Scope.Add("read:packages");
                    options.Scope.Add("write:packages");

                    options.Events.OnCreatingTicket = async context =>
                    {
                        await OAuthAccountCallbacks.ProcessGitHubLoginAsync(context);
                        OperationalLog.Record(context.HttpContext.RequestServices.GetRequiredService<ILoggerFactory>()
                                .CreateLogger("AutoMate.Security.Authentication"), AuditOperation.Authentication,
                            AuditOutcome.Prepared);
                    };

                    options.Events.OnRemoteFailure = context =>
                    {
                        OperationalLog.Record(context.HttpContext.RequestServices.GetRequiredService<ILoggerFactory>()
                                .CreateLogger("AutoMate.Security.Authentication"), AuditOperation.Authentication,
                            AuditOutcome.Failed);
                        return Task.CompletedTask;
                    };
                })
                .AddOAuth("Microsoft", options =>
                {
                    var tenantId =
                        OAuthAccountCallbacks.GetMicrosoftAuthorityTenant(config["Authentication:Microsoft:TenantId"]);

                    options.ClientId = config["Authentication:Microsoft:ClientId"]
                                       ?? throw new InvalidOperationException(
                                           "Authentication:Microsoft:ClientId is missing from configuration.");
                    options.ClientSecret = config["Authentication:Microsoft:ClientSecret"]
                                           ?? throw new InvalidOperationException(
                                               "Authentication:Microsoft:ClientSecret is missing from configuration.");

                    options.CallbackPath = new PathString("/signin-microsoft");
                    options.AuthorizationEndpoint =
                        $"https://login.microsoftonline.com/{tenantId}/oauth2/v2.0/authorize";
                    options.TokenEndpoint = $"https://login.microsoftonline.com/{tenantId}/oauth2/v2.0/token";

                    options.Scope.Add("openid");
                    options.Scope.Add("profile");
                    options.Scope.Add("email");
                    options.Scope.Add("offline_access");
                    options.Scope.Add(OAuthAccountCallbacks.AzureManagementScope);

                    options.SaveTokens = true;

                    options.Events.OnRedirectToAuthorizationEndpoint = context =>
                    {
                        context.Response.Redirect(QueryHelpers.AddQueryString(context.RedirectUri, "prompt",
                            "consent"));
                        return Task.CompletedTask;
                    };

                    options.Events.OnCreatingTicket = async context =>
                    {
                        await OAuthAccountCallbacks.ProcessMicrosoftLoginAsync(context);
                        OperationalLog.Record(context.HttpContext.RequestServices.GetRequiredService<ILoggerFactory>()
                                .CreateLogger("AutoMate.Security.Authentication"), AuditOperation.Authentication,
                            AuditOutcome.Prepared);
                    };

                    options.Events.OnRemoteFailure = context =>
                    {
                        OperationalLog.Record(context.HttpContext.RequestServices.GetRequiredService<ILoggerFactory>()
                                .CreateLogger("AutoMate.Security.Authentication"), AuditOperation.Authentication,
                            AuditOutcome.Failed);
                        return Task.CompletedTask;
                    };

                    options.Events.OnTicketReceived = OAuthAccountCallbacks.CompleteMicrosoftConnectionAsync;
                });

            // Add a cascading authentication state provider
            services.AddCascadingAuthenticationState();
            services.AddSingleton<IAuthorizationMiddlewareResultHandler, SecurityAuditResultHandler>();

            services.AddAuthorizationBuilder()
                .SetFallbackPolicy(new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build());

            // Rate Limiting Configured by IP or Authenticated User
            services.AddRateLimiter(options =>
            {
                options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(context =>
                {
                    var partitionKey = context.User.Identity?.IsAuthenticated == true
                        ? context.User.FindFirstValue(ClaimTypes.NameIdentifier)
                          ?? context.User.Identity.Name
                          ?? "authenticated_unknown"
                        : context.Connection.RemoteIpAddress?.ToString() ?? "unknown_ip";

                    // Create a fixed window rate limiter that allows 100 requests per minute for each partition key.
                    return RateLimitPartition.GetFixedWindowLimiter(partitionKey, _ => new FixedWindowRateLimiterOptions
                    {
                        AutoReplenishment = true,
                        PermitLimit = 100,
                        Window = TimeSpan.FromMinutes(1)
                    });
                });

                options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;

                options.OnRejected = (context, _) =>
                {
                    var authenticationState = context.HttpContext.User.Identity?.IsAuthenticated == true
                        ? "authenticated"
                        : "anonymous";

                    using var activity = AutoMateTelemetry.Security.StartActivity("security.rate_limit.rejected");
                    activity?.SetTag("security.authentication_state", authenticationState);

                    var tags = new TagList { { "security.authentication_state", authenticationState } };

                    AutoMateTelemetry.RateLimitRejections.Add(1, tags);

                    OperationalLog.Record(context.HttpContext.RequestServices.GetRequiredService<ILoggerFactory>()
                        .CreateLogger("AutoMate.Security.RateLimiting"), AuditOperation.RateLimit, AuditOutcome.Denied);

                    context.HttpContext.RequestServices.GetRequiredService<ILoggerFactory>()
                        .CreateLogger("AutoMate.Security.RateLimiting")
                        .LogWarning("Rate limit rejected request. Authentication state {AuthenticationState}.",
                            authenticationState);

                    return ValueTask.CompletedTask;
                };
            });
        }

        /// <summary>
        ///     Registers UI and API presentation dependencies.
        /// </summary>
        private void AddPresentation()
        {
            builder.Services.AddRazorComponents().AddInteractiveServerComponents();
            builder.Services.AddSignalR();
            builder.Services.AddEndpointsApiExplorer();
            builder.Services.AddSwaggerGen();
        }
    }


    /// <summary>
    ///     Extension method to register core services.
    /// </summary>
    /// <param name="services">The service collection to extend.</param>
    extension(IServiceCollection services)
    {
        /// <summary>
        ///     Registers core domain and application services into the DI container.
        /// </summary>
        /// <param name="capabilities">Server-enforced hosting-profile capabilities.</param>
        private void RegisterDomainServices(IDeploymentCapabilities capabilities)
        {
            // Core/Auth Services
            services.AddScoped<IAuthService, AuthService>();
            services.AddScoped<IPasswordHasher<LocalUser>, PasswordHasher<LocalUser>>();

            // Orchestration & Docker. Local services are intentionally absent from SaaS instances.
            if (capabilities.LocalDeploymentsEnabled)
            {
                services.AddScoped<DockerService>();
                services.AddScoped<IDockerService>(sp => sp.GetRequiredService<DockerService>());
                services.AddScoped<IDockerDiagnosticSource>(sp => sp.GetRequiredService<DockerService>());
                services.AddSingleton<LocalDeploymentLogStreamManager>();

                services.AddSingleton<ILocalDeploymentDiagnostics>(sp =>
                    sp.GetRequiredService<LocalDeploymentLogStreamManager>());

                services.AddHostedService(sp => sp.GetRequiredService<LocalDeploymentLogStreamManager>());
                services.AddScoped<ILocalDeploymentOrchestrator, LocalDeploymentOrchestrator>();
                services.AddHostedService<LocalRuntimeRecoveryService>();
            }
            else
            {
                services.AddScoped<IDockerService, DisabledDockerService>();
            }

            services.AddScoped<ICloudDeploymentOrchestrator, CloudDeploymentOrchestrator>();
            services.AddScoped<AzureArmCredentialsProvider>();
            services.AddScoped<ICloudDeploymentRunService, CloudDeploymentRunService>();

            if (capabilities is { LocalDeploymentsEnabled: false, CloudDeploymentsEnabled: true })
            {
                services.AddScoped<IGitHubWebhookReceiver, GitHubWebhookReceiver>();
                services.AddScoped<CloudRunProcessor>();
                services.AddScoped<CloudRunMonitor>();
                services.AddHostedService<CloudDeploymentScheduler>();
                services.AddHostedService<CloudRunMonitorService>();
                services.AddHostedService<CloudRunRetentionService>();
                services.AddHostedService<CloudRunMetricsService>();
            }

            services.AddSingleton<IDeploymentJobQueue, DeploymentJobQueue>();
            if (capabilities.LocalDeploymentsEnabled)
                services.AddHostedService<DeploymentJobWorker>();

            services.AddScoped<IAzureDeploymentOrchestrator, AzureDeploymentOrchestrator>();
            services.AddSingleton<AzureMonitorLogsTokenProvider>();

            services.AddSingleton<IAzureMonitorLogsTokenProvider>(serviceProvider =>
                serviceProvider.GetRequiredService<AzureMonitorLogsTokenProvider>());

            services.AddSingleton<AzureContainerAppRuntimeStreamer>();

            services.AddSingleton<IAzureContainerAppRuntimeStreamer>(serviceProvider =>
                serviceProvider.GetRequiredService<AzureContainerAppRuntimeStreamer>());

            services.AddHostedService(serviceProvider =>
                serviceProvider.GetRequiredService<AzureContainerAppRuntimeStreamer>());

            services.AddSingleton<IDeploymentStatusNotifier, DeploymentStatusNotifier>();

            if (capabilities.LocalDeploymentsEnabled)
                services.AddHostedService<DeploymentCleanupHostedService>();

            services.AddSingleton<ILogStreamer, RealTimeLogStreamer>();
            services.AddSingleton<IDiagnosticRedactor, DiagnosticRedactor>();
            services.AddSingleton(TimeProvider.System);
            services.AddSingleton<IDeploymentRuntimeViewers, DeploymentRuntimeViewers>();
            services.AddSingleton<DeploymentDiagnosticPublisher>();

            services.AddSingleton<IDeploymentDiagnosticPublisher>(serviceProvider =>
                serviceProvider.GetRequiredService<DeploymentDiagnosticPublisher>());

            services.AddHostedService<DeploymentDiagnosticDispatcher>();
            services.AddHostedService<DeploymentDiagnosticRetentionService>();

            // Business & Utilities
            services.AddScoped<ScopedApplicationOperations>();
            services.AddScoped<ConnectedCloudDeploymentAdmission>();
            services.AddScoped<CloudDeploymentAdmission>();
            services.AddScoped<IApplicationService, ApplicationService>();
            services.AddScoped<IWorkspaceQuery, WorkspaceQuery>();
            services.AddScoped<ProjectDetailsQuery>();

            services.AddScoped<IProjectDetailsQuery>(provider => provider.GetRequiredService<ProjectDetailsQuery>());
            services.AddScoped<IProjectActionQuery>(provider => provider.GetRequiredService<ProjectDetailsQuery>());
            services.AddScoped<IUserService, UserService>();

            services.AddScoped<ILocalSystemScannerService, LocalSystemScannerService>();
            services.AddScoped<IProjectScannerService, ProjectScannerService>();
            services.AddScoped<ITemplatingService, TemplatingService>();
            services.AddScoped<IEmailSenderService, GmailSenderService>();
        }
    }
}
