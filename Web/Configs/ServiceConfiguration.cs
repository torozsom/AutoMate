using System.Threading.RateLimiting;
using Core.Entities;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Services.Auth;
using Services.Data;
using Services.Docker;
using Services.Email;
using Services.GitHub;
using Services.Orchestration;
using Services.Projects;
using Services.Scanner;
using Services.Templating;
using Web.Extensions;
using Web.Routes.Endpoints;
using Web.Routes.Endpoints.Auth;

namespace Web.Configs;

/// <summary>
///     Provides a centralized location for configuring the services of the ASP.NET Core web application.
/// </summary>
public static class ServiceConfiguration
{
    /// <summary>
    ///     Configures the services for the ASP.NET Core web application.
    ///     This method is responsible for registering the application's services with the dependency injection container,
    ///     including the database context, authentication services, and Razor components.
    /// </summary>
    /// <param name="builder">The WebApplicationBuilder used to configure services.</param>
    /// <exception cref="InvalidOperationException">
    ///     Thrown when required configuration values are missing (e.g., GitHub
    ///     ClientId/ClientSecret).
    /// </exception>
    public static void Configure(WebApplicationBuilder builder)
    {
        // Add logging services to the services container.
        builder.Services.AddLogging();

        // Add data protection services to the services container.
        builder.Services.AddDataProtection()
            .PersistKeysToDbContext<AutoMateDbContext>()
            .SetApplicationName("AutoMate");

        // Add the database context to the services container, using PostgreSQL as the database provider.
        builder.Services.AddDbContext<AutoMateDbContext>(options =>
            options.UseNpgsql(builder.Configuration.GetConnectionString("DefaultConnection")));

        // Add authentication services to the container, configuring cookie authentication
        // as the default scheme and GitHub as an external authentication provider.
        builder.Services.AddAuthentication(options =>
            {
                options.DefaultScheme = CookieAuthenticationDefaults.AuthenticationScheme;
                options.DefaultChallengeScheme = "GitHub";
            })
            .AddCookie()
            .AddGitHub(options =>
            {
                options.ClientId = builder.Configuration["GitHub:ClientId"] ??
                                   throw new InvalidOperationException("GitHub:ClientId is required");

                options.ClientSecret = builder.Configuration["GitHub:ClientSecret"] ??
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
                    await authService.CreateOrUpdateGitHubUserAsync(githubId, username, email, avatarUrl, accessToken);
                };
            });

        // Add a cascading authentication state provider to the services container, which allows
        // components to access the current authentication state and react to changes in authentication status.
        builder.Services.AddCascadingAuthenticationState();

        // Add rate limiting services to the container
        builder.Services.AddRateLimiter(options =>
        {
            options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(context =>
            {
                // Use the user's identity name as the partition key if authenticated, otherwise use the remote IP address.
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

        // Add authorization services to the container, setting a fallback policy
        // that requires all users to be authenticated by default.
        builder.Services.AddAuthorizationBuilder()
            .SetFallbackPolicy(new AuthorizationPolicyBuilder()
                .RequireAuthenticatedUser()
                .Build());

        // Add services to the container.
        builder.Services.AddRazorComponents()
            .AddInteractiveServerComponents();

        // Add services for Swagger/OpenAPI
        builder.Services.AddEndpointsApiExplorer();
        builder.Services.AddSwaggerGen();

        // Add services for caching, using Redis as the distributed cache provider.
        builder.Services.AddStackExchangeRedisCache(options =>
        {
            options.Configuration = builder.Configuration.GetConnectionString("Redis");
            options.InstanceName = "AutoMate_";
        });


        // Add services for authentication and user management
        builder.Services.AddScoped<IAuthService, AuthService>();

        // Add a password hasher for hashing user passwords securely.
        builder.Services.AddScoped<IPasswordHasher<LocalUser>, PasswordHasher<LocalUser>>();

        // Add services for Docker operations
        builder.Services.AddScoped<IDockerService, DockerService>();

        // Add services for sending emails
        builder.Services.AddScoped<IEmailSenderService, GmailSenderService>();

        // Add services for GitHub API interactions
        builder.Services.AddHttpClient<IGitHubService, GitHubService>();

        // Add services for orchestrating deployments
        builder.Services.AddScoped<ILocalDeploymentOrchestrator, LocalDeploymentOrchestrator>();

        // Add a hosted service to periodically clean up deployment artifacts.
        builder.Services.AddHostedService<DeploymentCleanupHostedService>();

        // Add services for project management
        builder.Services.AddScoped<IProjectService, ProjectService>();

        // Add services for scanning local repositories
        builder.Services.AddScoped<ILocalSystemScannerService, LocalSystemScannerService>();

        // Add services for scanning projects for references and dependencies
        builder.Services.AddScoped<IProjectScannerService, ProjectScannerService>();

        // Add services for templating
        builder.Services.AddScoped<ITemplateService, TemplateService>();


        // Register endpoints
        builder.Services.AddEndpoint<StaticAssetsEndpoint>()
            .AddEndpoint<RazorComponentsEndpoint>()
            .AddEndpoint<GitHubLoginEndpoint>()
            .AddEndpoint<LoginEndpoint>()
            .AddEndpoint<LogoutEndpoint>();
    }
}