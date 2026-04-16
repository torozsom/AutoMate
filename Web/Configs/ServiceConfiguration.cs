using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.EntityFrameworkCore;
using Services.Auth;
using Services.Data;
using Services.Docker;
using Services.Email;
using Services.GitHub;
using Services.Projects;
using Services.Scanner;
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

        // Add services for sending emails
        builder.Services.AddScoped<IEmailSender, GmailEmailSender>();

        // Add services for authentication and user management
        builder.Services.AddScoped<IAuthService, AuthService>();

        // Add services for GitHub API interactions
        builder.Services.AddHttpClient<IGitHubService, GitHubService>();

        // Add services for scanning local repositories
        builder.Services.AddScoped<ILocalSystemScannerService, LocalSystemScannerService>();

        // Add services for project management
        builder.Services.AddScoped<IProjectService, ProjectService>();

        // Add services for Docker operations
        builder.Services.AddScoped<IDockerService, DockerService>();

        // Register endpoints
        builder.Services.AddEndpoint<StaticAssetsEndpoint>()
            .AddEndpoint<RazorComponentsEndpoint>()
            .AddEndpoint<GitHubLoginEndpoint>()
            .AddEndpoint<LoginEndpoint>()
            .AddEndpoint<LogoutEndpoint>();
    }
}