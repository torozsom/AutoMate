using System.Threading.RateLimiting;
using Core.Entities;
using Microsoft.AspNetCore.Authentication.Cookies;
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
using Web.Routes.Endpoints;
using Web.Routes.Endpoints.Auth;
using Web.Services;

namespace Web.Configs;

/// <summary>
///     Provides a centralized location for configuring the services of the ASP.NET Core web application.
/// </summary>
public static class ServiceConfiguration
{
    /// <summary>
    ///     Configures the services for the ASP.NET Core web application.
    ///     This method is responsible for registering the application's services with the dependency injection container.
    /// </summary>
    /// <param name="builder">The WebApplicationBuilder used to configure services.</param>
    /// <returns>The original WebApplicationBuilder for chaining.</returns>
    /// <exception cref="InvalidOperationException">
    ///     Thrown when required configuration values are missing (e.g., GitHub ClientId/ClientSecret).
    /// </exception>
    public static WebApplicationBuilder AddApplicationServices(this WebApplicationBuilder builder)
    {
        var services = builder.Services;
        var configuration = builder.Configuration;

        services.Configure<ForwardedHeadersOptions>(options =>
        {
            options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
            options.KnownIPNetworks.Clear();
            options.KnownProxies.Clear();
        });

        // Add logging services
        services.AddLogging();

        // Add antiforgery services for CSRF protection
        services.AddAntiforgery();

        // Configuration bindings (Options Pattern)
        services.Configure<EmailOptions>(configuration.GetSection(EmailOptions.SectionName));
        services.Configure<DockerOptions>(configuration.GetSection(DockerOptions.SectionName));

        // Add data protection services
        services.AddDataProtection()
            .PersistKeysToDbContext<AutoMateDbContext>()
            .SetApplicationName("AutoMate");

        // Add the database context
        services.AddDbContextPool<AutoMateDbContext>(options =>
            options.UseNpgsql(configuration.GetConnectionString("DefaultConnection"))
                .UseSnakeCaseNamingConvention());

        // Add authentication services (Cookie & GitHub OAuth)
        services.AddAuthentication(options =>
            {
                options.DefaultScheme = CookieAuthenticationDefaults.AuthenticationScheme;
                options.DefaultChallengeScheme = "GitHub";
            })
            .AddCookie()
            .AddGitHub(options =>
            {
                options.ClientId = configuration["GitHub:ClientId"] ??
                                   throw new InvalidOperationException("GitHub:ClientId is required");

                options.ClientSecret = configuration["GitHub:ClientSecret"] ??
                                       throw new InvalidOperationException("GitHub:ClientSecret is required");

                options.CallbackPath = new PathString("/signin-github");
                options.Scope.Add("user:email");
                options.Scope.Add("repo");

                options.Events.OnCreatingTicket = async context =>
                {
                    var githubId = context.User.GetProperty("id").GetInt32().ToString();
                    var username = context.User.GetProperty("login").GetString() ?? "Unknown";
                    var email = context.User.GetProperty("email").GetString() ?? "no-email@github.com";

                    var avatarUrl = context.User.TryGetProperty("avatar_url", out var avatarElem)
                        ? avatarElem.GetString()
                        : null;
                    var accessToken = context.AccessToken;

                    var authService = context.HttpContext.RequestServices.GetRequiredService<IAuthService>();

                    // Create or update the GitHub user in the database and return the local user record.
                    await authService.CreateOrUpdateGitHubUserAsync(githubId, username, email, avatarUrl, accessToken,
                        context.HttpContext.RequestAborted);
                };
            });

        // Add a cascading authentication state provider
        services.AddCascadingAuthenticationState();

        // Add rate limiting services
        services.AddRateLimiter(options =>
        {
            options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(context =>
            {
                // Now context.Connection.RemoteIpAddress will be correct thanks to ForwardedHeaders!
                var partitionKey = context.User.Identity?.IsAuthenticated == true
                    ? context.User.Identity.Name!
                    : context.Connection.RemoteIpAddress?.ToString() ?? "unknown";

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

        // Add authorization services
        services.AddAuthorizationBuilder()
            .SetFallbackPolicy(new AuthorizationPolicyBuilder()
                .RequireAuthenticatedUser()
                .Build());

        // Add Blazor UI services
        services.AddRazorComponents()
            .AddInteractiveServerComponents();

        // Add services for Swagger/OpenAPI
        services.AddEndpointsApiExplorer();
        services.AddSwaggerGen();

        // Add Redis for distributed caching
        services.AddStackExchangeRedisCache(options =>
        {
            options.Configuration = configuration.GetConnectionString("Redis");
            options.InstanceName = "AutoMate_";
        });

        // Add SignalR services
        services.AddSignalR();

        // Application Specific Services Registration
        RegisterApplicationServices(services);

        // Endpoint Registration
        services.AddEndpoint<StaticAssetsEndpoint>()
            .AddEndpoint<RazorComponentsEndpoint>()
            .AddEndpoint<GitHubLoginEndpoint>()
            .AddEndpoint<LoginEndpoint>()
            .AddEndpoint<LogoutEndpoint>();

        return builder;
    }


    /// <summary>
    ///     Registers custom business logic services into the DI container.
    /// </summary>
    private static void RegisterApplicationServices(IServiceCollection services)
    {
        services.AddScoped<ILogStreamer, RealTimeLogStreamer>();
        services.AddScoped<IAuthService, AuthService>();
        services.AddScoped<IPasswordHasher<LocalUser>, PasswordHasher<LocalUser>>();
        services.AddScoped<IDockerService, DockerService>();
        services.AddScoped<IEmailSenderService, GmailSenderService>();
        services.AddHttpClient<IGitHubService, GitHubService>();
        services.AddSingleton<IDeploymentStatusNotifier, DeploymentStatusNotifier>();
        services.AddScoped<ILocalDeploymentOrchestrator, LocalDeploymentOrchestrator>();
        services.AddHostedService<DeploymentCleanupHostedService>();
        services.AddScoped<IProjectService, ProjectService>();
        services.AddScoped<ILocalSystemScannerService, LocalSystemScannerService>();
        services.AddScoped<IProjectScannerService, ProjectScannerService>();
        services.AddScoped<ITemplatingService, TemplatingService>();
    }
}