using System.Security.Claims;
using Core.Entities;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Services.Data;

namespace Web.Routes.Endpoints;

public class LoginEndpoint : IEndpoint
{
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
                new Claim(ClaimTypes.NameIdentifier, user.Id.ToString()),
                new Claim(ClaimTypes.Name, user.Username),
                new Claim(ClaimTypes.Email, user.Email)
            };

            var identity = new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme);
            var principal = new ClaimsPrincipal(identity);

            await context.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, principal);
            context.Response.Redirect("/");

        }).DisableAntiforgery();
    }
}