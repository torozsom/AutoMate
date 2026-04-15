using Core.Entities;

namespace Services.Auth;

/// <summary>
///     Service interface for handling user authentication, registration, and email verification.
/// </summary>
public interface IAuthService
{
    /// <summary>
    ///     Registers a new user with the specified details.
    /// </summary>
    /// <param name="username">The username for the new user.</param>
    /// <param name="email">The email address for the new user.</param>
    /// <param name="password">The plain-text password for the new user.</param>
    /// <param name="verificationLinkFactory">A function that takes a verification token and returns a full verification URL.</param>
    /// <returns>
    ///     A task that represents the asynchronous operation. Returns true if registration was successful; otherwise,
    ///     false if the email already exists.
    /// </returns>
    Task<bool> RegisterAsync(string username, string email, string password,
        Func<string, string> verificationLinkFactory);

    /// <summary>
    ///     Verifies a user's email address using a verification token.
    /// </summary>
    /// <param name="token">The verification token.</param>
    /// <returns>
    ///     A task that represents the asynchronous operation. Returns true if verification was successful; otherwise,
    ///     false.
    /// </returns>
    Task<bool> VerifyEmailAsync(string token);

    /// <summary>
    ///     Authenticates a user with the specified email and password.
    /// </summary>
    /// <param name="email">The email address of the user.</param>
    /// <param name="password">The plain-text password of the user.</param>
    /// <returns>
    ///     A task that represents the asynchronous operation. Returns the authenticated user if successful; otherwise,
    ///     null.
    /// </returns>
    Task<(LocalUser? User, string? ErrorMessage)> LoginAsync(string email, string password);

    /// <summary>
    ///     Creates or updates a GitHub user in the database.
    /// </summary>
    /// <param name="githubId">The unique GitHub account ID.</param>
    /// <param name="username">The GitHub username.</param>
    /// <param name="email">The GitHub user's email.</param>
    /// <param name="avatarUrl">The URL of the user's GitHub avatar.</param>
    /// <param name="accessToken">The GitHub access token.</param>
    /// <returns>A task that represents the asynchronous operation.</returns>
    Task CreateOrUpdateGitHubUserAsync(string githubId, string username, string email, string? avatarUrl,
        string? accessToken);
}