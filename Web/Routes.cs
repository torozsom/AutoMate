using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Web.Components;

namespace Web;

/// <summary>
/// Provides a centralized location for configuring application routes and endpoints.
/// This class handles the registration of static assets, Razor components, and authentication endpoints.
/// </summary>
public static class Routes
{
    /// <summary>
    /// Configures the HTTP request pipeline by mapping the application's routes.
    /// This includes setting up static file serving, interactive server-side rendering for Blazor components,
    /// and defining authentication endpoints for login and logout operations.
    /// </summary>
    /// <param name="app">The <see cref="WebApplication"/> instance representing the running application.</param>
    public static void Configure(WebApplication app)
    {
        // Map static assets (like CSS, JavaScript, and images) to be served directly from the wwwroot folder.
        app.MapStaticAssets();

        // Map Razor components to the root URL ("/") and configure them to use interactive server render mode,
        // which allows for dynamic, server-rendered components that can update in real-time without requiring a full page refresh.
        app.MapRazorComponents<App>().AddInteractiveServerRenderMode();

        // Map a GET endpoint for "/login" that initiates the authentication challenge
        // using the GitHub authentication scheme, redirecting the user to the GitHub
        // login page and then back to the root URL ("/") upon successful authentication.
        app.MapGet("/login", () =>
            Results.Challenge(
                new AuthenticationProperties { RedirectUri = "/" },
                ["GitHub"])
        );

        // Map a POST endpoint for "/logout" that signs the user out of the application
        // by clearing the authentication cookie and then redirects them back to the root.
        app.MapPost("/logout", async (HttpContext context) =>
        {
            await context.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
            return Results.Redirect("/");
        });
    }
}