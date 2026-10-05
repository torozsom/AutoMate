using System.Security.Claims;
using Application.Auth;
using Application.Diagnostics;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Mvc;

namespace Web.Routes.Endpoints.Auth;

/// <summary>
///     Endpoint for handling local user login via form post.
/// </summary>
/// <remarks>
///     IMPORTANT: Requires a valid Antiforgery Token from the frontend.
///     In Blazor, ensure your login form includes the <AntiforgeryToken /> component.
/// </remarks>
public sealed class LoginEndpoint : IEndpoint
{
    /// <inheritdoc />
    public void Map(IEndpointRouteBuilder app)
    {
        app.MapPost("/api/auth/login", async (
                HttpContext context,
                [FromServices] IAuthService authService,
                ILogger<LoginEndpoint> logger,
                [FromForm] string email,
                [FromForm] string password) =>
            {
                logger.LogInformation("Login attempt started.");
                if (string.IsNullOrWhiteSpace(email) || string.IsNullOrWhiteSpace(password))
                {
                    OperationalLog.Record(logger, AuditOperation.Authentication, AuditOutcome.Denied);
                    return Results.LocalRedirect("/login?error=Email%20and%20password%20are%20required");
                }

                var (user, errorMessage) = await authService.LoginAsync(email, password, context.RequestAborted);

                if (user == null)
                {
                    OperationalLog.Record(logger, AuditOperation.Authentication, AuditOutcome.Denied);
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

                OperationalLog.Record(logger, AuditOperation.Authentication, AuditOutcome.Completed);
                logger.LogInformation("Login completed for user {UserId}.", user.Id);
                return Results.LocalRedirect("/"); // Safe internal redirect
            })
            .AllowAnonymous();
    }
}
