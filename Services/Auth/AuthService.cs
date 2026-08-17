using System.Security.Cryptography;
using System.Text;
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
public sealed class AuthService(
    AutoMateDbContext dbContext,
    IEmailSenderService emailSenderService,
    IPasswordHasher<LocalUser> passwordHasher,
    ILogger<AuthService> logger) : IAuthService
{
    /// <summary>
    ///     The verification token lifetime used for local email registrations.
    /// </summary>
    private const int VerificationTokenLifetimeHours = 24;

    /// <summary>
    ///     Generic login failure text that avoids revealing which credential failed.
    /// </summary>
    private const string InvalidCredentialsMessage = "Invalid credentials";

    /// <summary>
    ///     Login failure text returned when a local account has not confirmed its email address.
    /// </summary>
    private const string EmailNotVerifiedMessage = "Email not verified";

    /// <summary>
    ///     Fallback display name for remote profiles with no usable username.
    /// </summary>
    private const string UnknownRemoteUsername = "Unknown";

    /// <summary>
    ///     Subject line used for local registration verification emails.
    /// </summary>
    private const string VerificationEmailSubject = "Confirm your registration to AutoMate!";

    /// <inheritdoc />
    public async Task<bool> RegisterAsync(string username, string email, string password,
        Func<string, string> verificationLinkFactory, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(verificationLinkFactory);

        if (!TryCreateRegistrationInput(username, email, password, out var registration))
        {
            logger.LogWarning("[AuthService] Registration failed because required fields were empty.");
            return false;
        }

        if (await IsEmailInUseAsync(registration.Email, cancellationToken))
        {
            logger.LogWarning("[AuthService] Registration failed: email is already in use for username '{Username}'.",
                registration.Username);
            return false;
        }

        var newUser = CreateLocalUserEntity(registration);

        try
        {
            dbContext.Users.Add(newUser);
            await dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException ex)
        {
            logger.LogWarning(ex, "[AuthService] Registration failed while saving user '{Username}'.",
                registration.Username);
            return false;
        }

        var isEmailSent = await TrySendVerificationEmailAsync(newUser, verificationLinkFactory, cancellationToken);
        if (!isEmailSent)
        {
            await RemoveUnverifiedUserAsync(newUser, cancellationToken);
            return false;
        }

        logger.LogInformation("[AuthService] Successfully registered new user '{Username}'.", registration.Username);
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
            logger.LogWarning("[AuthService] Email verification failed for token fingerprint '{TokenFingerprint}'.",
                CreateTokenFingerprint(token));
            return false;
        }

        MarkEmailVerified(user);

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
            return (null, InvalidCredentialsMessage);

        var user = await dbContext.Users
            .OfType<LocalUser>()
            .FirstOrDefaultAsync(u => u.Email == normalizedEmail, cancellationToken);

        if (user?.PasswordHash == null)
            return (null, InvalidCredentialsMessage);

        var verificationResult = passwordHasher.VerifyHashedPassword(user, user.PasswordHash, password);

        if (verificationResult == PasswordVerificationResult.Failed)
            return (null, InvalidCredentialsMessage);

        if (!user.IsEmailVerified)
            return (null, EmailNotVerifiedMessage);

        if (verificationResult == PasswordVerificationResult.SuccessRehashNeeded)
        {
            RehashPassword(user, password);
            await dbContext.SaveChangesAsync(cancellationToken);
        }

        return (user, null);
    }


    /// <inheritdoc />
    public async Task CreateOrUpdateGitHubUserAsync(string githubId, string username, string email, string? avatarUrl,
        string? accessToken, CancellationToken cancellationToken = default)
    {
        var profile = NormalizeGitHubProfile(githubId, username, email, avatarUrl, accessToken);

        var existingUser = await dbContext.Users
            .OfType<RemoteUser>()
            .FirstOrDefaultAsync(u => u.AccountId == profile.AccountId, cancellationToken);

        if (existingUser == null)
        {
            dbContext.Users.Add(CreateRemoteUser(profile));
            logger.LogInformation("[AuthService] Created new GitHub user: {Username}", profile.Username);
        }
        else
        {
            ApplyGitHubProfile(existingUser, profile);
            logger.LogInformation("[AuthService] Updated existing GitHub user: {Username}", profile.Username);
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
        var azureConnection = new AzureAccountConnection(
            azureAccountId,
            tenantId,
            subscriptionId,
            accessToken,
            refreshToken,
            expiresAt);

        var user = await FindCurrentUserAsync(currentUserIdentifier, cancellationToken);

        if (user == null)
        {
            logger.LogWarning(
                "[AuthService] Azure account linking failed because current user '{Identifier}' was not found.",
                currentUserIdentifier);
            return;
        }

        ApplyAzureConnection(user, azureConnection);

        await dbContext.SaveChangesAsync(cancellationToken);

        logger.LogInformation("[AuthService] Linked Azure account '{AzureAccountId}' to user '{UserId}'.",
            azureConnection.AccountId, user.Id);
    }


    /// <summary>
    ///     Resolves the current remote user from either the persisted user ID or the GitHub account ID claim.
    /// </summary>
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
    ///     Checks whether any account already owns the normalized email address.
    /// </summary>
    private async Task<bool> IsEmailInUseAsync(string email, CancellationToken cancellationToken)
    {
        return await dbContext.Users.AnyAsync(u => u.Email == email, cancellationToken);
    }

    /// <summary>
    ///     Creates a local user with a hashed password and verification token ready for persistence.
    /// </summary>
    private LocalUser CreateLocalUserEntity(RegistrationInput registration)
    {
        var user = new LocalUser
        {
            Email = registration.Email,
            Username = registration.Username,
            IsEmailVerified = false,
            EmailVerificationToken = GenerateSecureToken(),
            VerificationTokenExpiry = DateTimeOffset.UtcNow.AddHours(VerificationTokenLifetimeHours)
        };

        user.PasswordHash = passwordHasher.HashPassword(user, registration.Password);
        return user;
    }

    /// <summary>
    ///     Sends the registration verification email and converts email delivery failures into a false result.
    /// </summary>
    private async Task<bool> TrySendVerificationEmailAsync(LocalUser user, Func<string, string> verificationLinkFactory,
        CancellationToken cancellationToken = default)
    {
        var verificationLink = verificationLinkFactory(user.EmailVerificationToken!);

        try
        {
            await emailSenderService.SendEmailAsync(
                user.Email,
                VerificationEmailSubject,
                CreateVerificationEmailBody(verificationLink),
                cancellationToken);

            return true;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "[AuthService] Failed to send verification email.");
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
        return Base64UrlEncode(tokenBytes);
    }

    /// <summary>
    ///     Removes a newly created unverified user after verification email delivery fails.
    /// </summary>
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

    /// <summary>
    ///     Normalizes email addresses before persistence and comparison.
    /// </summary>
    private static string NormalizeEmail(string email)
    {
        return email.Trim().ToLowerInvariant();
    }

    /// <summary>
    ///     Validates and normalizes local registration input without throwing for normal user mistakes.
    /// </summary>
    private static bool TryCreateRegistrationInput(string username, string email, string password,
        out RegistrationInput registration)
    {
        var normalizedUsername = username.Trim();
        var normalizedEmail = NormalizeEmail(email);

        registration = new RegistrationInput(normalizedUsername, normalizedEmail, password);

        return !string.IsNullOrWhiteSpace(registration.Username) &&
               !string.IsNullOrWhiteSpace(registration.Email) &&
               !string.IsNullOrWhiteSpace(registration.Password);
    }

    /// <summary>
    ///     Normalizes GitHub OAuth profile values before they are applied to a remote user.
    /// </summary>
    private static GitHubProfile NormalizeGitHubProfile(string githubId, string username, string email,
        string? avatarUrl, string? accessToken)
    {
        return new GitHubProfile(
            githubId,
            string.IsNullOrWhiteSpace(username) ? UnknownRemoteUsername : username.Trim(),
            NormalizeEmail(email),
            avatarUrl,
            accessToken);
    }

    /// <summary>
    ///     Creates a new remote user entity from a normalized GitHub profile.
    /// </summary>
    private static RemoteUser CreateRemoteUser(GitHubProfile profile)
    {
        return new RemoteUser
        {
            AccountId = profile.AccountId,
            Username = profile.Username,
            Email = profile.Email,
            AvatarUrl = profile.AvatarUrl,
            GitHubAccessToken = profile.AccessToken
        };
    }

    /// <summary>
    ///     Updates GitHub-owned profile fields on an existing remote user.
    /// </summary>
    private static void ApplyGitHubProfile(RemoteUser user, GitHubProfile profile)
    {
        user.Username = profile.Username;
        user.Email = profile.Email;
        user.AvatarUrl = profile.AvatarUrl;
        user.GitHubAccessToken = profile.AccessToken;
    }

    /// <summary>
    ///     Applies the latest Azure OAuth connection data to a remote user.
    /// </summary>
    private static void ApplyAzureConnection(RemoteUser user, AzureAccountConnection connection)
    {
        user.AzureAccountId = connection.AccountId;
        user.AzureTenantId = connection.TenantId;
        user.AzureSubscriptionId = connection.SubscriptionId;
        user.AzureAccessToken = connection.AccessToken;
        user.AzureRefreshToken = connection.RefreshToken;
        user.AzureTokenExpiresAt = connection.ExpiresAt;
    }

    /// <summary>
    ///     Marks a local user as verified and clears single-use verification token data.
    /// </summary>
    private static void MarkEmailVerified(LocalUser user)
    {
        user.IsEmailVerified = true;
        user.EmailVerificationToken = null;
        user.VerificationTokenExpiry = null;
    }

    /// <summary>
    ///     Refreshes a password hash when the configured hasher reports that rehashing is needed.
    /// </summary>
    private void RehashPassword(LocalUser user, string password)
    {
        user.PasswordHash = passwordHasher.HashPassword(user, password);
    }

    /// <summary>
    ///     Builds the plain-text registration verification email body.
    /// </summary>
    private static string CreateVerificationEmailBody(string verificationLink)
    {
        return $"Welcome to AutoMate!\n\nPlease follow this link for verification:\n{verificationLink}";
    }

    /// <summary>
    ///     Creates a short non-reversible token fingerprint for logging failed verification attempts.
    /// </summary>
    private static string CreateTokenFingerprint(string token)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(token));
        return Base64UrlEncode(hash[..8]);
    }

    /// <summary>
    ///     Encodes binary data using URL-safe Base64 without padding.
    /// </summary>
    private static string Base64UrlEncode(ReadOnlySpan<byte> bytes)
    {
        return Convert.ToBase64String(bytes)
            .Replace("+", "-")
            .Replace("/", "_")
            .TrimEnd('=');
    }

    /// <summary>
    ///     Normalized local registration values used after input validation succeeds.
    /// </summary>
    private readonly record struct RegistrationInput(string Username, string Email, string Password);

    /// <summary>
    ///     Normalized GitHub profile values used when creating or updating remote users.
    /// </summary>
    private readonly record struct GitHubProfile(
        string AccountId,
        string Username,
        string Email,
        string? AvatarUrl,
        string? AccessToken);

    /// <summary>
    ///     Azure account connection values persisted for a remote user.
    /// </summary>
    private readonly record struct AzureAccountConnection(
        string AccountId,
        string? TenantId,
        string? SubscriptionId,
        string? AccessToken,
        string? RefreshToken,
        DateTimeOffset? ExpiresAt);
}