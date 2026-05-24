using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;

namespace Web.Routes.Endpoints.Auth;

/// <summary>
///     Endpoint for connecting an Azure account to the current AutoMate user.
/// </summary>
public class AzureLoginEndpoint : IEndpoint
{
    /// <inheritdoc />
    public void Map(IEndpointRouteBuilder app)
    {
        app.MapGet("/api/auth/azure-login", (HttpContext context) =>
        {
            var userIdentifier = context.User.FindFirstValue(ClaimTypes.NameIdentifier);

            if (string.IsNullOrWhiteSpace(userIdentifier))
                return Results.Forbid();

            var properties = new AuthenticationProperties
            {
                RedirectUri = "/dashboard",
                Items = { ["automate_user_id"] = userIdentifier }
            };

            return Results.Challenge(properties, ["Microsoft"]);
        }).RequireAuthorization();
    }
}
