using System.Diagnostics;
using System.Security.Claims;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using System.Threading.RateLimiting;
using Application.Abstractions.Ai;
using Application.Abstractions.Azure;
using Application.Abstractions.Diagnostics;
using Application.Abstractions.Docker;
using Application.Abstractions.Email;
using Application.Abstractions.GitHub;
using Application.Abstractions.Hosting;
using Application.Abstractions.Logging;
using Application.Abstractions.Scanning;
using Application.Abstractions.Templating;
using Application.Ai;
using Application.Auth;
using Application.Data.Apps;
using Application.Data.Users;
using Application.Diagnostics;
using Application.Orchestration;
using Domain.Entities;
using Infrastructure.Ai;
using Infrastructure.ApplicationServices.Data.Apps;
using Infrastructure.ApplicationServices.Orchestration;
using Infrastructure.Azure;
using Infrastructure.Data;
using Infrastructure.Diagnostics;
using Infrastructure.Docker;
using Infrastructure.Email;
using Infrastructure.GitHub;
using Infrastructure.Observability;
using Infrastructure.Scanner;
using Infrastructure.Templating;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OAuth;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;
using Web.Extensions;
using Web.Observability;
using Web.Services;

namespace Web.Configs;

/// <summary>
///     Provides a centralized, modular configuration for ASP.NET Core dependency injection.
///     Ensures separation of concerns by splitting infrastructure, security, and presentation setups.
/// </summary>
public static class ServiceConfiguration
{
    private const string DefaultConnectionKey = "DefaultConnection";
    private const string RedisConnectionKey = "Redis";
    private const string AppName = "AutoMate";
    private const string AutoMateUserIdAuthProperty = "automate_user_id";
    private const string AzureConnectionRedirectUri = "/dashboard";
    private const string AzureManagementScope = "https://management.azure.com/.default";
    private const string AzureMonitorLogsScope = "https://api.loganalytics.io/.default";
    private const string DefaultMicrosoftAuthorityTenant = "organizations";


    /// <summary>
    ///     Resolves the Microsoft identity authority tenant used for Azure account connection.
    /// </summary>
    /// <param name="configuredTenant">The optional tenant configured for local development or single-tenant installs.</param>
    /// <returns>The configured tenant, or a multi-tenant organizations authority by default.</returns>
    private static string GetMicrosoftAuthorityTenant(string? configuredTenant)
    {
        if (string.IsNullOrWhiteSpace(configuredTenant))
            return DefaultMicrosoftAuthorityTenant;

        var tenant = configuredTenant.Trim();
        return tenant.Equals("common", StringComparison.OrdinalIgnoreCase) ||
               tenant.Equals("organizations", StringComparison.OrdinalIgnoreCase) ||
               tenant.Equals("consumers", StringComparison.OrdinalIgnoreCase) ||
               Guid.TryParse(tenant, out _)
            ? tenant
            : DefaultMicrosoftAuthorityTenant;
    }


    /// <summary>
    ///     Helper method to extract GitHub OAuth mapping logic, improving readability and testability.
    /// </summary>
    private static async Task ProcessGitHubLoginAsync(OAuthCreatingTicketContext context)
    {
        var githubId = context.User.GetProperty("id").GetInt64().ToString();
        var username = context.User.GetProperty("login").GetString() ?? "Unknown";
        var email = context.User.GetProperty("email").GetString();

        if (string.IsNullOrWhiteSpace(email))
            email = $"{githubId}@users.noreply.github.com";

        var avatarUrl = context.User.TryGetProperty("avatar_url", out var avatarElem)
            ? avatarElem.GetString()
            : null;

        var accessToken = context.AccessToken;

        var authService = context.HttpContext.RequestServices.GetRequiredService<IAuthService>();

        // Call domain service to persist user
        await authService.CreateOrUpdateGitHubUserAsync(
            githubId, username, email, avatarUrl, accessToken, context.HttpContext.RequestAborted);
    }


    /// <summary>
    ///     Extracts Microsoft identity data from the OAuth callback and links it to the current AutoMate user.
    /// </summary>
    private static async Task ProcessMicrosoftLoginAsync(OAuthCreatingTicketContext context)
    {
        if (!context.Properties.Items.TryGetValue(AutoMateUserIdAuthProperty, out var currentUserIdentifier) ||
            string.IsNullOrWhiteSpace(currentUserIdentifier))
            return;

        var idToken = GetTokenResponseString(context, "id_token");

        var azureAccountId = GetString(context.User, "sub")
                             ?? GetString(context.User, "oid")
                             ?? JwtPayloadReader.GetStringValue(idToken, "sub")
                             ?? JwtPayloadReader.GetStringValue(idToken, "oid")
                             ?? string.Empty;

        if (string.IsNullOrWhiteSpace(azureAccountId))
            return;

        var displayName = GetString(context.User, "name")
                          ?? JwtPayloadReader.GetStringValue(idToken, "name")
                          ?? "Azure user";

        var email = GetString(context.User, "email")
                    ?? GetString(context.User, "preferred_username")
                    ?? JwtPayloadReader.GetStringValue(idToken, "email")
                    ?? JwtPayloadReader.GetStringValue(idToken, "preferred_username")
                    ?? "no-email@microsoft.com";

        var tenantId = JwtPayloadReader.GetStringValue(idToken, "tid");
        var azureManagementToken = await ResolveAzureManagementTokenAsync(context);
        var subscriptionId = await AzureSubscriptionResolver.GetDefaultSubscriptionIdAsync(
            azureManagementToken,
            context.HttpContext.RequestServices.GetRequiredService<IHttpClientFactory>(),
            context.HttpContext.RequestAborted);

        var expiresAt = GetTokenExpiresAt(context);

        var authService = context.HttpContext.RequestServices.GetRequiredService<IAuthService>();

        await authService.LinkAzureAccountAsync(
            currentUserIdentifier,
            azureAccountId,
            email,
            displayName,
            tenantId,
            subscriptionId,
            azureManagementToken,
            context.RefreshToken,
            expiresAt,
            context.HttpContext.RequestAborted);
    }


    /// <summary>
    ///     Prevents the Azure connect callback from replacing the existing AutoMate authentication cookie.
    /// </summary>
    private static Task CompleteMicrosoftConnectionAsync(TicketReceivedContext context)
    {
        if (context.Properties?.Items.ContainsKey(AutoMateUserIdAuthProperty) == true)
        {
            context.HandleResponse();
            context.Response.Redirect(AzureConnectionRedirectUri);
        }

        return Task.CompletedTask;
    }


    /// <summary>
    ///     Reads a string property from a JSON element.
    /// </summary>
    private static string? GetString(JsonElement source, string propertyName)
    {
        return source.TryGetProperty(propertyName, out var property) ? property.GetString() : null;
    }


    /// <summary>
    ///     Reads a string value from the OAuth token response payload.
    /// </summary>
    private static string? GetTokenResponseString(OAuthCreatingTicketContext context, string propertyName)
    {
        return context.TokenResponse.Response?.RootElement.TryGetProperty(propertyName, out var property) == true
            ? property.GetString()
            : null;
    }


    /// <summary>
    ///     Calculates the access token expiry time from the OAuth token response.
    /// </summary>
    private static DateTimeOffset? GetTokenExpiresAt(OAuthCreatingTicketContext context)
    {
        return context.TokenResponse.Response?.RootElement.TryGetProperty("expires_in", out var expiresInElement) ==
               true &&
               expiresInElement.TryGetInt32(out var expiresIn)
            ? DateTimeOffset.UtcNow.AddSeconds(expiresIn)
            : null;
    }


    /// <summary>
    ///     Exchanges the OAuth refresh token for an Azure Resource Manager-scoped access token.
    /// </summary>
    private static async Task<string?> ResolveAzureManagementTokenAsync(OAuthCreatingTicketContext context)
    {
        var refreshToken = context.RefreshToken;
        var tokenEndpoint = context.Options.TokenEndpoint;
        var clientId = context.Options.ClientId;
        var clientSecret = context.Options.ClientSecret;

        if (string.IsNullOrWhiteSpace(refreshToken) ||
            string.IsNullOrWhiteSpace(tokenEndpoint) ||
            string.IsNullOrWhiteSpace(clientId) ||
            string.IsNullOrWhiteSpace(clientSecret))
            return null;

        var httpClientFactory = context.HttpContext.RequestServices.GetRequiredService<IHttpClientFactory>();
        using var httpClient = httpClientFactory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, tokenEndpoint);
        request.Content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["client_id"] = clientId,
            ["client_secret"] = clientSecret,
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = refreshToken,
            ["scope"] = AzureManagementScope
        });

        using var response = await httpClient.SendAsync(request, context.HttpContext.RequestAborted);
        if (!response.IsSuccessStatusCode)
            return null;

        await using var stream = await response.Content.ReadAsStreamAsync(context.HttpContext.RequestAborted);

        using var payload = await JsonDocument.ParseAsync(stream,
            cancellationToken: context.HttpContext.RequestAborted);

        return payload.RootElement.TryGetProperty("access_token", out var tokenElement)
            ? tokenElement.GetString()
            : null;
    }


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
            builder.Services.Configure<AzureMonitorLogsOptions>(options =>
            {
                var tenant = GetMicrosoftAuthorityTenant(builder.Configuration["Authentication:Microsoft:TenantId"]);
                options.TokenEndpoint = $"https://login.microsoftonline.com/{tenant}/oauth2/v2.0/token";
                options.ClientId = builder.Configuration["Authentication:Microsoft:ClientId"] ?? string.Empty;
                options.ClientSecret = builder.Configuration["Authentication:Microsoft:ClientSecret"] ?? string.Empty;
                options.Scope = AzureMonitorLogsScope;
            });

            var hostingProfile = builder.Configuration.GetSection(HostingProfileOptions.SectionName)
                .Get<HostingProfileOptions>() ?? new HostingProfileOptions();
            var capabilities = hostingProfile.ToCapabilities();
            if (!capabilities.LocalDeploymentsEnabled && capabilities.CloudDeploymentsEnabled)
                builder.Services.AddOptions<GitHubAppOptions>()
                    .Validate(options => options.AppId > 0 &&
                                         !string.IsNullOrWhiteSpace(options.AppSlug) &&
                                         !string.IsNullOrWhiteSpace(options.PrivateKeyPem) &&
                                         !string.IsNullOrWhiteSpace(options.WebhookSecret),
                        "SaaS requires a configured GitHub App ID, slug, signing key, and webhook secret.")
                    .ValidateOnStart();
            builder.Services.AddSingleton(capabilities);
            builder.Services.AddSingleton<IDeploymentCapabilities>(capabilities);
            return capabilities;
        }


        /// <summary>Registers OpenTelemetry for AutoMate's own requests, providers, and deployment diagnostics.</summary>
        private void AddObservability()
        {
            var options = builder.Configuration.GetSection(OpenTelemetryOptions.SectionName)
                .Get<OpenTelemetryOptions>() ?? new OpenTelemetryOptions();
            var exportConsole = options.ExportConsole;
            var hasOtlpEndpoint =
                OpenTelemetryOptionsValidator.TryGetCollectorEndpoint(options.OtlpEndpoint, out var otlpEndpoint);
            var serviceVersion = typeof(ServiceConfiguration).Assembly.GetName().Version?.ToString() ?? "unknown";

            builder.Services.AddSafePlatformLogging();

            builder.Logging.AddOpenTelemetry(logging =>
            {
                logging.IncludeFormattedMessage = true;
                logging.IncludeScopes = true;
                logging.AddProcessor(services =>
                    new SafeLogProcessor(services.GetRequiredService<PlatformTelemetryPolicy>()));
                if (exportConsole) logging.AddConsoleExporter();
                if (hasOtlpEndpoint) logging.AddOtlpExporter(exporter => exporter.Endpoint = otlpEndpoint!);
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
                    if (hasOtlpEndpoint) tracing.AddOtlpExporter(exporter => exporter.Endpoint = otlpEndpoint!);
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
                    if (hasOtlpEndpoint) metrics.AddOtlpExporter(exporter => exporter.Endpoint = otlpEndpoint!);
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
        /// <remarks>
        ///     IMPORTANT: Proxy configuration assumes the application is hosted behind a trusted reverse proxy.
        /// </remarks>
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
                        await ProcessGitHubLoginAsync(context);
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
                    var tenantId = GetMicrosoftAuthorityTenant(config["Authentication:Microsoft:TenantId"]);

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
                    options.Scope.Add(AzureManagementScope);

                    options.SaveTokens = true;

                    options.Events.OnCreatingTicket = async context =>
                    {
                        await ProcessMicrosoftLoginAsync(context);
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
                    options.Events.OnTicketReceived = CompleteMicrosoftConnectionAsync;
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
            if (!capabilities.LocalDeploymentsEnabled && capabilities.CloudDeploymentsEnabled)
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
            services.AddSingleton<TimeProvider>(TimeProvider.System);
            services.AddSingleton<IDeploymentRuntimeViewers, DeploymentRuntimeViewers>();
            services.AddSingleton<DeploymentDiagnosticPublisher>();
            services.AddSingleton<IDeploymentDiagnosticPublisher>(serviceProvider =>
                serviceProvider.GetRequiredService<DeploymentDiagnosticPublisher>());
            services.AddHostedService<DeploymentDiagnosticDispatcher>();
            services.AddHostedService<DeploymentDiagnosticRetentionService>();

            // Business & Utilities
            services.AddScoped<IApplicationService, ApplicationService>();
            services.AddScoped<IUserService, UserService>();
            services.AddScoped<ILocalSystemScannerService, LocalSystemScannerService>();
            services.AddScoped<IProjectScannerService, ProjectScannerService>();
            services.AddScoped<ITemplatingService, TemplatingService>();
            services.AddScoped<IEmailSenderService, GmailSenderService>();
        }
    }
}