using System.Diagnostics;
using System.Security.Claims;
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
using Infrastructure.Azure;
using Infrastructure.Data;
using Infrastructure.Diagnostics;
using Infrastructure.Docker;
using Infrastructure.Email;
using Infrastructure.GitHub;
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
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using Web.Extensions;
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
            builder.Services.AddHealthChecks();

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
            builder.Services.Configure<DeploymentDiagnosticOptions>(
                builder.Configuration.GetSection(DeploymentDiagnosticOptions.SectionName));
            builder.Services.Configure<GitHubWorkflowMonitoringOptions>(
                builder.Configuration.GetSection(GitHubWorkflowMonitoringOptions.SectionName));
            builder.Services.AddOptions<DeploymentConcurrencyOptions>()
                .Bind(builder.Configuration.GetSection(DeploymentConcurrencyOptions.SectionName))
                .Validate(options => options.MaxLocalBuilds is >= -1 and <= 1_024 &&
                                     options.MaxCloudDeployments is >= 1 and <= 16 &&
                                     options.MaxQueuedJobs is >= 1 and <= 1_000,
                    "Deployment concurrency limits must be within their supported ranges.")
                .ValidateOnStart();
            builder.Services.Configure<OpenTelemetryOptions>(
                builder.Configuration.GetSection(OpenTelemetryOptions.SectionName));
            builder.Services.Configure<AiAnalysisOptions>(
                builder.Configuration.GetSection(AiAnalysisOptions.SectionName));
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
            var hasOtlpEndpoint = Uri.TryCreate(options.OtlpEndpoint, UriKind.Absolute, out var otlpEndpoint);
            var serviceVersion = typeof(ServiceConfiguration).Assembly.GetName().Version?.ToString() ?? "unknown";

            builder.Logging.AddOpenTelemetry(logging =>
            {
                logging.IncludeFormattedMessage = true;
                logging.IncludeScopes = true;
                if (exportConsole) logging.AddConsoleExporter();
                if (hasOtlpEndpoint) logging.AddOtlpExporter(exporter => exporter.Endpoint = otlpEndpoint!);
            });

            builder.Services.AddOpenTelemetry()
                .ConfigureResource(resource => resource
                    .AddService(options.ServiceName, serviceVersion: serviceVersion)
                    .AddAttributes([
                        new KeyValuePair<string, object>("deployment.environment",
                            options.Environment ?? builder.Environment.EnvironmentName)
                    ]))
                .WithTracing(tracing =>
                {
                    tracing.AddAspNetCoreInstrumentation();
                    tracing.AddHttpClientInstrumentation();
                    tracing.AddEntityFrameworkCoreInstrumentation();
                    tracing.AddSource(AutoMateTelemetry.Deployments.Name);
                    tracing.AddSource(AutoMateTelemetry.Security.Name);
                    if (exportConsole) tracing.AddConsoleExporter();
                    if (hasOtlpEndpoint) tracing.AddOtlpExporter(exporter => exporter.Endpoint = otlpEndpoint!);
                })
                .WithMetrics(metrics =>
                {
                    metrics.AddAspNetCoreInstrumentation();
                    metrics.AddHttpClientInstrumentation();
                    metrics.AddRuntimeInstrumentation();
                    metrics.AddMeter(AutoMateTelemetry.DeploymentMeter.Name);
                    metrics.AddMeter(AutoMateTelemetry.SecurityMeter.Name);
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
            services.AddHttpClient<ILlmAnalysisProvider, OpenAiAnalysisProvider>()
                .AddStandardResilienceHandler();
            services.AddScoped<IDeploymentDiagnosticStore, DeploymentDiagnosticStore>();
            services.AddScoped<IDeploymentAnalysisService, DeploymentAnalysisService>();
            services.AddScoped<IDeploymentAnalysisQueue, DeploymentAnalysisQueue>();
            services.AddHostedService<DeploymentAnalysisWorker>();
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
            services.AddDataProtection()
                .PersistKeysToDbContext<AutoMateDbContext>()
                .SetApplicationName(AppName);

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

                    options.Events.OnCreatingTicket = async context => await ProcessGitHubLoginAsync(context);
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

                    options.Events.OnCreatingTicket = async context => await ProcessMicrosoftLoginAsync(context);
                    options.Events.OnTicketReceived = CompleteMicrosoftConnectionAsync;
                });

            // Add a cascading authentication state provider
            services.AddCascadingAuthenticationState();
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
                services.AddScoped<IDockerService, DockerService>();
                services.AddScoped<ILocalDeploymentOrchestrator, LocalDeploymentOrchestrator>();
            }
            else
            {
                services.AddScoped<IDockerService, DisabledDockerService>();
            }

            services.AddScoped<ICloudDeploymentOrchestrator, CloudDeploymentOrchestrator>();
            services.AddSingleton<IDeploymentJobQueue, DeploymentJobQueue>();
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
            services.AddHostedService<DeploymentCleanupHostedService>();
            services.AddSingleton<ILogStreamer, RealTimeLogStreamer>();
            services.AddSingleton<IDiagnosticRedactor, DiagnosticRedactor>();
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
