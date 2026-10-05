using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using System.Security.Claims;

namespace Web.Routes.Endpoints.Auth;

/// <summary>
///     Endpoint for logging out the user. Clears the authentication cookie.
/// </summary>
public sealed class LogoutEndpoint : IEndpoint
{
    /// <inheritdoc />
    public void Map(IEndpointRouteBuilder app)
    {
        app.MapPost("/api/auth/logout", async (HttpContext context, ILogger<LogoutEndpoint> logger) =>
            {
                var userId = Guid.TryParse(context.User.FindFirstValue(ClaimTypes.NameIdentifier), out var id)
                    ? id : (Guid?)null;
                await context.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
                logger.LogInformation("Logout completed for user {UserId}.", userId);
                return Results.LocalRedirect("/");
            })
            // A logout should ideally require an authenticated user.
            .RequireAuthorization();
    }
}
