using Core.Entities;

namespace Services.Auth;

/// <summary>
///     Service interface for handling user authentication, registration, and email verification.
/// </summary>
public interface IAuthService
{
    /// Registers a new user with the specified username, email, and password.
    Task<bool> RegisterAsync(string username, string email, string password,
        Func<string, string> verificationLinkFactory);

    /// Verifies a user's email using the provided token.
    Task<bool> VerifyEmailAsync(string token);

    /// Authenticates a user with the specified email and password.
    Task<(LocalUser? User, string? ErrorMessage)> LoginAsync(string email, string password);

    /// Creates or updates a GitHub user in the database.
    Task CreateOrUpdateGitHubUserAsync(string githubId, string username, string email, string? avatarUrl,
        string? accessToken);
}