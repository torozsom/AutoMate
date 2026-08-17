using Microsoft.AspNetCore.Authentication;

namespace Web.Routes.Endpoints.Auth;

/// <summary>
///     Endpoint for handling GitHub user login. Initiates the OAuth authentication challenge.
/// </summary>
public sealed class GitHubLoginEndpoint : IEndpoint
{
    /// <inheritdoc />
    public void Map(IEndpointRouteBuilder app)
    {
        app.MapGet("/api/auth/github-login", () =>
            Results.Challenge(
                new AuthenticationProperties { RedirectUri = "/" },
                ["GitHub"])
        ).AllowAnonymous();
    }
}