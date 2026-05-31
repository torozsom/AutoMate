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
    private const int VerificationTokenLifetimeHours = 24;

    /// <inheritdoc />
    public async Task<bool> RegisterAsync(string username, string email, string password,
        Func<string, string> verificationLinkFactory, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(verificationLinkFactory);

        var normalizedEmail = NormalizeEmail(email);
        var normalizedUsername = username.Trim();

        if (string.IsNullOrWhiteSpace(normalizedUsername) ||
            string.IsNullOrWhiteSpace(normalizedEmail) ||
            string.IsNullOrWhiteSpace(password))
        {
            logger.LogWarning("[AuthService] Registration failed because required fields were empty.");
            return false;
        }

        if (await IsEmailInUseAsync(normalizedEmail, cancellationToken))
        {
            logger.LogWarning("[AuthService] Registration failed: email is already in use for username '{Username}'.",
                normalizedUsername);
            return false;
        }

        var newUser = CreateLocalUserEntity(normalizedUsername, normalizedEmail, password);

        try
        {
            dbContext.Users.Add(newUser);
            await dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException ex)
        {
            logger.LogWarning(ex, "[AuthService] Registration failed while saving user '{Username}'.",
                normalizedUsername);
            return false;
        }

        var isEmailSent = await TrySendVerificationEmailAsync(newUser, verificationLinkFactory, cancellationToken);
        if (!isEmailSent)
        {
            await RemoveUnverifiedUserAsync(newUser, cancellationToken);
            return false;
        }

        logger.LogInformation("[AuthService] Successfully registered new user '{Username}'.", normalizedUsername);
        return true;
    }


    /// <inheritdoc />
    public async Task<bool> VerifyEmailAsync(string token, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(token))
            return false;

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
        var normalizedEmail = NormalizeEmail(email);
        if (string.IsNullOrWhiteSpace(normalizedEmail) || string.IsNullOrWhiteSpace(password))
            return (null, "Invalid credentials");

        var user = await dbContext.Users
            .OfType<LocalUser>()
            .FirstOrDefaultAsync(u => u.Email == normalizedEmail, cancellationToken);

        if (user?.PasswordHash == null)
            return (null, "Invalid credentials");

        var verificationResult = passwordHasher.VerifyHashedPassword(user, user.PasswordHash, password);

        if (verificationResult == PasswordVerificationResult.Failed)
            return (null, "Invalid credentials");

        if (!user.IsEmailVerified)
            return (null, "Email not verified");

        if (verificationResult == PasswordVerificationResult.SuccessRehashNeeded)
        {
            user.PasswordHash = passwordHasher.HashPassword(user, password);
            await dbContext.SaveChangesAsync(cancellationToken);
        }

        return (user, null);
    }


    /// <inheritdoc />
    public async Task CreateOrUpdateGitHubUserAsync(string githubId, string username, string email, string? avatarUrl,
        string? accessToken, CancellationToken cancellationToken = default)
    {
        var normalizedEmail = NormalizeEmail(email);
        var normalizedUsername = string.IsNullOrWhiteSpace(username) ? "Unknown" : username.Trim();

        var existingUser = await dbContext.Users
            .OfType<RemoteUser>()
            .FirstOrDefaultAsync(u => u.AccountId == githubId, cancellationToken);

        if (existingUser == null)
        {
            var newUser = new RemoteUser
            {
                AccountId = githubId,
                Username = normalizedUsername,
                Email = normalizedEmail,
                AvatarUrl = avatarUrl,
                GitHubAccessToken = accessToken
            };

            dbContext.Users.Add(newUser);
            logger.LogInformation("[AuthService] Created new GitHub user: {Username}", normalizedUsername);
        }
        else
        {
            existingUser.Username = normalizedUsername;
            existingUser.Email = normalizedEmail;
            existingUser.AvatarUrl = avatarUrl;
            existingUser.GitHubAccessToken = accessToken;

            logger.LogInformation("[AuthService] Updated existing GitHub user: {Username}", normalizedUsername);
        }

        await dbContext.SaveChangesAsync(cancellationToken);
    }


    /// <inheritdoc />
    public async Task LinkAzureAccountAsync(
        string currentUserIdentifier,
        string azureAccountId,
        string email,
        string displayName,
        string? tenantId,
        string? subscriptionId,
        string? accessToken,
        string? refreshToken,
        DateTimeOffset? expiresAt,
        CancellationToken cancellationToken = default)
    {
        var user = await FindCurrentUserAsync(currentUserIdentifier, cancellationToken);

        if (user == null)
        {
            logger.LogWarning(
                "[AuthService] Azure account linking failed because current user '{Identifier}' was not found.",
                currentUserIdentifier);
            return;
        }

        user.AzureAccountId = azureAccountId;
        user.AzureTenantId = tenantId;
        user.AzureSubscriptionId = subscriptionId;
        user.AzureAccessToken = accessToken;
        user.AzureRefreshToken = refreshToken;
        user.AzureTokenExpiresAt = expiresAt;

        await dbContext.SaveChangesAsync(cancellationToken);

        logger.LogInformation("[AuthService] Linked Azure account '{AzureAccountId}' to user '{UserId}'.",
            azureAccountId, user.Id);
    }


    /// <summary>
    ///     Resolves the current user from either the local user ID claim or the GitHub account ID claim.
    /// </summary>
    /// <param name="identifier">The current authenticated user identifier.</param>
    /// <param name="cancellationToken">Propagates notification that operations should be canceled.</param>
    /// <returns>The current user entity, or null when no matching user is found.</returns>
    private async Task<RemoteUser?> FindCurrentUserAsync(string identifier, CancellationToken cancellationToken)
    {
        if (Guid.TryParse(identifier, out var userId))
            return await dbContext.Users
                .OfType<RemoteUser>()
                .FirstOrDefaultAsync(u => u.Id == userId, cancellationToken);

        return await dbContext.Users
            .OfType<RemoteUser>()
            .FirstOrDefaultAsync(u => u.AccountId == identifier, cancellationToken);
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
            VerificationTokenExpiry = DateTimeOffset.UtcNow.AddHours(VerificationTokenLifetimeHours)
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
            logger.LogError(ex, "[AuthService] Failed to send verification email'.");
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

    private async Task RemoveUnverifiedUserAsync(LocalUser user, CancellationToken cancellationToken)
    {
        try
        {
            dbContext.Users.Remove(user);
            await dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException ex)
        {
            logger.LogError(ex,
                "[AuthService] Verification email failed and cleanup failed for user '{UserId}'.", user.Id);
        }
    }

    private static string NormalizeEmail(string email)
    {
        return email.Trim().ToLowerInvariant();
    }
}