using System.Security.Cryptography;
using Core.Entities;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Services.Data;
using Services.Email;

namespace Services.Auth;

/// <summary>
///     Service implementation for handling user authentication, registration, and email verification.
/// </summary>
public class AuthService(
    AutoMateDbContext dbContext,
    IEmailSenderService emailSenderService,
    IPasswordHasher<LocalUser> passwordHasher,
    ILogger<AuthService> logger) : IAuthService
{
    /// <inheritdoc />
    public async Task<bool> RegisterAsync(string username, string email, string password,
        Func<string, string> verificationLinkFactory, CancellationToken cancellationToken = default)
    {
        if (await IsEmailInUseAsync(email, cancellationToken))
        {
            logger.LogWarning("[AuthService] Registration failed: email is already in use for username '{Username}'.",
                username);
            return false;
        }

        var newUser = CreateLocalUserEntity(username, email, password);

        await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);
        try
        {
            dbContext.Users.Add(newUser);
            await dbContext.SaveChangesAsync(cancellationToken);

            var isEmailSent = await TrySendVerificationEmailAsync(newUser, verificationLinkFactory, cancellationToken);
            if (!isEmailSent)
            {
                await transaction.RollbackAsync(cancellationToken);
                return false;
            }

            await transaction.CommitAsync(cancellationToken);
            logger.LogInformation("[AuthService] Successfully registered new user '{Username}'.", username);
            return true;
        }
        catch (Exception ex)
        {
            await transaction.RollbackAsync(cancellationToken);
            logger.LogCritical(ex,
                "[AuthService] CRITICAL: Transaction failed during user registration for '{Username}'.", username);
            return false;
        }
    }


    /// <inheritdoc />
    public async Task<bool> VerifyEmailAsync(string token, CancellationToken cancellationToken = default)
    {
        var user = await dbContext.Users
            .OfType<LocalUser>()
            .FirstOrDefaultAsync(u => u.EmailVerificationToken == token, cancellationToken);

        if (user == null || user.IsEmailVerified || user.VerificationTokenExpiry < DateTimeOffset.UtcNow)
        {
            var sanitizedTokenForLog = token.Replace("\r", string.Empty).Replace("\n", string.Empty);
            logger.LogWarning("[AuthService] Email verification failed for token '{Token}'.", sanitizedTokenForLog);
            return false;
        }

        user.IsEmailVerified = true;
        user.EmailVerificationToken = null;
        user.VerificationTokenExpiry = null;

        await dbContext.SaveChangesAsync(cancellationToken);

        logger.LogInformation("[AuthService] Email verified successfully for user '{Username}'.", user.Username);
        return true;
    }


    /// <inheritdoc />
    public async Task<(LocalUser? User, string? ErrorMessage)> LoginAsync(string email, string password,
        CancellationToken cancellationToken = default)
    {
        var user = await dbContext.Users
            .OfType<LocalUser>()
            .FirstOrDefaultAsync(u => u.Email == email, cancellationToken);

        if (user == null)
            return (null, "Invalid credentials");

        var verificationResult = passwordHasher.VerifyHashedPassword(user, user.PasswordHash!, password);

        if (verificationResult == PasswordVerificationResult.Failed)
            return (null, "Invalid credentials");

        if (!user.IsEmailVerified)
            return (null, "Email not verified");

        return (user, null);
    }


    /// <inheritdoc />
    public async Task CreateOrUpdateGitHubUserAsync(string githubId, string username, string email, string? avatarUrl,
        string? accessToken, CancellationToken cancellationToken = default)
    {
        var existingUser = await dbContext.Users
            .OfType<GitHubUser>()
            .FirstOrDefaultAsync(u => u.AccountId == githubId, cancellationToken);

        if (existingUser == null)
        {
            var newUser = new GitHubUser
            {
                AccountId = githubId,
                Username = username,
                Email = email,
                AvatarUrl = avatarUrl,
                AccessToken = accessToken
            };

            dbContext.Users.Add(newUser);
            logger.LogInformation("[AuthService] Created new GitHub user: {Username}", username);
        }
        else
        {
            existingUser.Username = username;
            existingUser.Email = email;
            existingUser.AvatarUrl = avatarUrl;
            existingUser.AccessToken = accessToken;

            logger.LogInformation("[AuthService] Updated existing GitHub user: {Username}", username);
        }

        await dbContext.SaveChangesAsync(cancellationToken);
    }


    /// <summary>
    ///     Checks if the provided email address is already associated with an existing user account in the database.
    /// </summary>
    /// <param name="email">The email to be checked if it is used already.</param>
    /// <param name="cancellationToken">Propagates notification that operations should be canceled.</param>
    /// <returns></returns>
    private async Task<bool> IsEmailInUseAsync(string email, CancellationToken cancellationToken)
    {
        return await dbContext.Users.AnyAsync(u => u.Email == email, cancellationToken);
    }


    /// <summary>
    ///     Creates a new instance of the <see cref="LocalUser" /> entity with the provided username, email, and password.
    ///     The method also generates a secure email verification token and sets the token's expiry time.
    /// </summary>
    /// <param name="username">The username of the local user.</param>
    /// <param name="email">The email of the local user.</param>
    /// <param name="password">The password of the local user.</param>
    /// <returns></returns>
    private LocalUser CreateLocalUserEntity(string username, string email, string password)
    {
        var user = new LocalUser
        {
            Email = email,
            Username = username,
            IsEmailVerified = false,
            EmailVerificationToken = GenerateSecureToken(),
            VerificationTokenExpiry = DateTimeOffset.UtcNow.AddHours(24)
        };

        user.PasswordHash = passwordHasher.HashPassword(user, password);
        return user;
    }


    /// <summary>
    ///     Attempts to send a verification email to the user with the provided email verification token.
    ///     If the email fails to send, the method logs the error and returns false, allowing the caller
    ///     to handle the failure appropriately.
    /// </summary>
    /// <param name="user">The user to be verified.</param>
    /// <param name="verificationLinkFactory">The functor that handles the link creation.</param>
    /// <param name="cancellationToken">Propagates notification that operations should be canceled.</param>
    /// <returns></returns>
    private async Task<bool> TrySendVerificationEmailAsync(LocalUser user, Func<string, string> verificationLinkFactory,
        CancellationToken cancellationToken = default)
    {
        var verificationLink = verificationLinkFactory(user.EmailVerificationToken!);

        try
        {
            await emailSenderService.SendEmailAsync(
                user.Email,
                "Confirm your registration to AutoMate!",
                $"Welcome to AutoMate!\n\nPlease follow this link for verification:\n{verificationLink}",
                cancellationToken);

            return true;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "[AuthService] Failed to send verification email to '{Email}'.", user.Email);
            return false;
        }
    }


    /// <summary>
    ///     Generates a secure random token for email verification purposes. The token is created
    ///     using a cryptographically secure random number generator and is encoded in a URL-safe
    ///     Base64 format, ensuring it can be safely included in email verification links without
    ///     issues related to special characters.
    /// </summary>
    /// <returns>A secure token for verification.</returns>
    private static string GenerateSecureToken()
    {
        var tokenBytes = RandomNumberGenerator.GetBytes(32);
        return Convert.ToBase64String(tokenBytes)
            .Replace("+", "-")
            .Replace("/", "_")
            .TrimEnd('=');
    }
}