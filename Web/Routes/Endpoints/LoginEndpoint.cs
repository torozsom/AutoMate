using Microsoft.AspNetCore.Authentication;

namespace Web.Routes.Endpoints;

/// <summary>
///     Endpoint for handling user login. When accessed, it initiates
///     the authentication process using GitHub as the provider.
/// </summary>
public class LoginEndpoint : IEndpoint
{
    public void Map(IEndpointRouteBuilder app)
    {
        app.MapGet("/login", () =>
            Results.Challenge(
                new AuthenticationProperties { RedirectUri = "/" },
                ["GitHub"])
        );
    }
}