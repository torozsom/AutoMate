using System.Threading.RateLimiting;
using Core.Entities;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OAuth;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Services.Auth;
using Services.Data;
using Services.Docker;
using Services.Email;
using Services.GitHub;
using Services.LogStreaming;
using Services.Orchestration;
using Services.Projects;
using Services.Scanner;
using Services.Templating;
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


    /// <summary>
    ///     Helper method to extract GitHub OAuth mapping logic, improving readability and testability.
    /// </summary>
    private static async Task ProcessGitHubLoginAsync(OAuthCreatingTicketContext context)
    {
        var githubId = context.User.GetProperty("id").GetInt32().ToString();
        var username = context.User.GetProperty("login").GetString() ?? "Unknown";
        var email = context.User.GetProperty("email").GetString() ?? "no-email@github.com";
        var avatarUrl = context.User.TryGetProperty("avatar_url", out var avatarElem) ? avatarElem.GetString() : null;
        var accessToken = context.AccessToken;

        var authService = context.HttpContext.RequestServices.GetRequiredService<IAuthService>();

        // Call domain service to persist user
        await authService.CreateOrUpdateGitHubUserAsync(
            githubId, username, email, avatarUrl, accessToken, context.HttpContext.RequestAborted);
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
            builder.AddConfigurations();

            // Add Infrastructure (DB, Redis, Clients)
            builder.AddInfrastructure();

            // Add Security (Auth, RateLimiting, DataProtection, Proxies)
            builder.AddSecurity();

            // Add Presentation Layer (Blazor, SignalR, Swagger)
            builder.AddPresentation();

            // Add Business Logic Services
            builder.Services.RegisterDomainServices();

            // Register Minimal API Endpoints
            builder.Services.AddEndpoints();

            return builder;
        }


        /// <summary>
        ///     Binds application settings to strongly-typed option classes using the Options Pattern.
        /// </summary>
        private void AddConfigurations()
        {
            builder.Services.Configure<EmailOptions>(builder.Configuration.GetSection(EmailOptions.SectionName));
            builder.Services.Configure<DockerOptions>(builder.Configuration.GetSection(DockerOptions.SectionName));
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
                .AddStandardResilienceHandler();
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
                .AddCookie()
                .AddGitHub(options =>
                {
                    options.ClientId = config["GitHub:ClientId"]
                                       ?? throw new InvalidOperationException(
                                           "GitHub:ClientId is missing from configuration.");
                    options.ClientSecret = config["GitHub:ClientSecret"]
                                           ?? throw new InvalidOperationException(
                                               "GitHub:ClientSecret is missing from configuration.");

                    options.CallbackPath = new PathString("/signin-github");
                    options.Scope.Add("user:email");
                    options.Scope.Add("repo");

                    options.Events.OnCreatingTicket = async context => await ProcessGitHubLoginAsync(context);
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
                        ? context.User.Identity.Name!
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
        private void RegisterDomainServices()
        {
            // Core/Auth Services
            services.AddScoped<IAuthService, AuthService>();
            services.AddScoped<IPasswordHasher<LocalUser>, PasswordHasher<LocalUser>>();

            // Orchestration & Docker
            services.AddScoped<IDockerService, DockerService>();
            services.AddScoped<ILocalDeploymentOrchestrator, LocalDeploymentOrchestrator>();
            services.AddSingleton<IDeploymentStatusNotifier, DeploymentStatusNotifier>();
            services.AddHostedService<DeploymentCleanupHostedService>();
            services.AddScoped<ILogStreamer, RealTimeLogStreamer>();

            // Business & Utilities
            services.AddScoped<IProjectService, ProjectService>();
            services.AddScoped<ILocalSystemScannerService, LocalSystemScannerService>();
            services.AddScoped<IProjectScannerService, ProjectScannerService>();
            services.AddScoped<ITemplatingService, TemplatingService>();
            services.AddScoped<IEmailSenderService, GmailSenderService>();
        }
    }
}