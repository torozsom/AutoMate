using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;

namespace Web.Routes.Endpoints.Auth;

/// <summary>
///     Endpoint for logging out the user. Clears the authentication cookie.
/// </summary>
public sealed class LogoutEndpoint : IEndpoint
{
    /// <inheritdoc />
    public void Map(IEndpointRouteBuilder app)
    {
        app.MapPost("/api/auth/logout", async (HttpContext context) =>
            {
                await context.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
                return Results.LocalRedirect("/");
            })
            // A logout should ideally require an authenticated user.
            .RequireAuthorization();
    }
}