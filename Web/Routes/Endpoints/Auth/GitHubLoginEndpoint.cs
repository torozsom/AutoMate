using Microsoft.AspNetCore.Authentication;

namespace Web.Routes.Endpoints.Auth;

/// <summary>
///     Endpoint for handling GitHub user login. When accessed, it initiates
///     the authentication process using GitHub as the provider.
/// </summary>
public class GitHubLoginEndpoint : IEndpoint
{
    public void Map(IEndpointRouteBuilder app)
    {
        app.MapGet("/api/auth/gh-login", () =>
            Results.Challenge(
                new AuthenticationProperties { RedirectUri = "/" },
                ["GitHub"])
        );
    }
}