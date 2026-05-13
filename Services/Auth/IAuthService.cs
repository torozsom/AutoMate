using Core.Entities;

namespace Services.Auth;

/// <summary>
///     Service interface for handling user authentication, registration, and email verification.
/// </summary>
public interface IAuthService
{
    /// <summary>
    ///     Registers a new user by creating their account, saving their details, and sending a verification email.
    /// </summary>
    /// <param name="username">The username of the user being registered.</param>
    /// <param name="email">The email address of the user being registered.</param>
    /// <param name="password">The password for the user's account (will be hashed).</param>
    /// <param name="verificationLinkFactory">Factory method to generate the email verification link.</param>
    /// <param name="cancellationToken">Propagates notification that operations should be canceled.</param>
    /// <returns>True if registration is successful; false if the email is already in use or sending fails.</returns>
    Task<bool> RegisterAsync(string username, string email, string password,
        Func<string, string> verificationLinkFactory, CancellationToken cancellationToken = default);


    /// <summary>
    ///     Verifies a user's email using the provided token.
    /// </summary>
    /// <param name="token">The secure email verification token.</param>
    /// <param name="cancellationToken">Propagates notification that operations should be canceled.</param>
    /// <returns>True if successful; false if token is invalid, expired, or user already verified.</returns>
    Task<bool> VerifyEmailAsync(string token, CancellationToken cancellationToken = default);


    /// <summary>
    ///     Authenticates a user with the specified email and password.
    /// </summary>
    /// <param name="email">The email address of the user.</param>
    /// <param name="password">The password provided by the user.</param>
    /// <param name="cancellationToken">Propagates notification that operations should be canceled.</param>
    /// <returns>A tuple containing the user if successful, or an error message.</returns>
    Task<(LocalUser? User, string? ErrorMessage)> LoginAsync(string email, string password,
        CancellationToken cancellationToken = default);


    /// <summary>
    ///     Creates or updates a GitHub user in the database.
    /// </summary>
    /// <param name="githubId">The unique GitHub account ID.</param>
    /// <param name="username">The username of the GitHub account.</param>
    /// <param name="email">The email associated with the GitHub account.</param>
    /// <param name="avatarUrl">The user's avatar URL.</param>
    /// <param name="accessToken">The GitHub OAuth access token.</param>
    /// <param name="cancellationToken">Propagates notification that operations should be canceled.</param>
    Task CreateOrUpdateGitHubUserAsync(string githubId, string username, string email, string? avatarUrl,
        string? accessToken, CancellationToken cancellationToken = default);
}