using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.EntityFrameworkCore;
using Services.Data;
using Web.Extensions;
using Web.Routes;
using Web.Routes.Endpoints;

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
    /// <param name="builder"></param>
    /// <exception cref="InvalidOperationException"></exception>
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

        // Register endpoints
        builder.Services.AddEndpoint<StaticAssetsEndpoint>()
            .AddEndpoint<RazorComponentsEndpoint>()
            .AddEndpoint<LoginEndpoint>()
            .AddEndpoint<LogoutEndpoint>();
    }
}