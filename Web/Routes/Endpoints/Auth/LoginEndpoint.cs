using System.Security.Claims;
using Core.Entities;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Services.Data;

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
            var dbContext = context.RequestServices.GetRequiredService<AutoMateDbContext>();
            var user = await dbContext.Users.OfType<LocalUser>().FirstOrDefaultAsync(u => u.Email == email);

            if (user == null)
            {
                context.Response.Redirect("/login?error=Invalid%20credentials");
                return;
            }

            var hasher = new PasswordHasher<LocalUser>();
            var verificationResult = hasher.VerifyHashedPassword(user, user.PasswordHash!, password);

            if (verificationResult == PasswordVerificationResult.Failed)
            {
                context.Response.Redirect("/login?error=Invalid%20credentials");
                return;
            }

            if (!user.IsEmailVerified)
            {
                context.Response.Redirect("/login?error=Email%20not%20verified");
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