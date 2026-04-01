using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Mvc;
using Services.Email;

namespace Web.Routes.Endpoints.Auth;


/// <summary>
///     Endpoint for handling user login. It accepts email and password
///     as form data, verifies the credentials against the database, and if valid,
///     signs the user in using cookie authentication. If the credentials are invalid
///     or the email is not verified, it redirects back to the login page with an error message.
/// </summary>
public class LoginEndpoint : IEndpoint
{
    /// <inheritdoc />
    public void Map(IEndpointRouteBuilder app)
    {
        app.MapPost("/api/auth/login", async (
            HttpContext context,
            [FromForm] string email,
            [FromForm] string password) =>
        {
            var authService = context.RequestServices.GetRequiredService<IAuthService>();
            var (user, errorMessage) = await authService.LoginAsync(email, password);

            if (user == null)
            {
                var encodedError = Uri.EscapeDataString(errorMessage ?? "Invalid credentials");
                context.Response.Redirect($"/login?error={encodedError}");
                return;
            }

            var claims = new List<Claim>
            {
                new(ClaimTypes.NameIdentifier, user.Id.ToString()),
                new(ClaimTypes.Name, user.Username),
                new(ClaimTypes.Email, user.Email)
            };

            var identity = new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme);
            var principal = new ClaimsPrincipal(identity);

            await context.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, principal);
            context.Response.Redirect("/");
        }).DisableAntiforgery();
    }
}