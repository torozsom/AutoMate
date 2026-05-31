using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Mvc;
using Services.Auth;

namespace Web.Routes.Endpoints.Auth;

/// <summary>
///     Endpoint for handling local user login via form post.
/// </summary>
/// <remarks>
///     IMPORTANT: Requires a valid Antiforgery Token from the frontend.
///     In Blazor, ensure your login form includes the <AntiforgeryToken /> component.
/// </remarks>
public class LoginEndpoint : IEndpoint
{
    /// <inheritdoc />
    public void Map(IEndpointRouteBuilder app)
    {
        app.MapPost("/api/auth/login", async (
                HttpContext context,
                [FromServices] IAuthService authService,
                [FromForm] string email,
                [FromForm] string password) =>
            {
                if (string.IsNullOrWhiteSpace(email) || string.IsNullOrWhiteSpace(password))
                    return Results.LocalRedirect("/login?error=Email%20and%20password%20are%20required");

                var (user, errorMessage) = await authService.LoginAsync(email, password, context.RequestAborted);

                if (user == null)
                {
                    var encodedError = Uri.EscapeDataString(errorMessage ?? "Invalid credentials");
                    // Using LocalRedirect to prevent Open Redirect vulnerability
                    return Results.LocalRedirect($"/login?error={encodedError}");
                }

                var claims = new List<Claim>
                {
                    new(ClaimTypes.NameIdentifier, user.Id.ToString()),
                    new(ClaimTypes.Name, user.Username),
                    new(ClaimTypes.Email, user.Email)
                };

                var identity = new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme);
                var principal = new ClaimsPrincipal(identity);
                var properties = new AuthenticationProperties
                {
                    AllowRefresh = true,
                    IsPersistent = false,
                    IssuedUtc = DateTimeOffset.UtcNow
                };

                await context.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, principal, properties);

                return Results.LocalRedirect("/"); // Safe internal redirect
            })
            .AllowAnonymous();
    }
}
