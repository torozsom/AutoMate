using System.Security.Cryptography;
using System.Text;
using Application.Abstractions.Email;
using Domain.Entities;
using Infrastructure.Data;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Application.Auth;

/// <summary>
///     Service implementation for handling user authentication, registration, and email
///     verification.
/// </summary>
/// <param name="dbContext">
///     Application metadata context used for ownership, leases and persisted state.
/// </param>
/// <param name="emailSenderService">
///     Provides the email sender service boundary used by this operation.
/// </param>
/// <param name="passwordHasher">
///     Hashes and verifies local-account passwords without retaining plaintext.
/// </param>
/// <param name="logger">
///     Receives reviewed operational events without diagnostic payloads or credentials.
/// </param>
public sealed class AuthService(
    AutoMateDbContext dbContext,
    IEmailSenderService emailSenderService,
    IPasswordHasher<LocalUser> passwordHasher,
    ILogger<AuthService> logger) : IAuthService
{
    /// <summary>The verification token lifetime used for local email registrations.</summary>
    private const int VerificationTokenLifetimeHours = 24;

    /// <summary>Generic login failure text that avoids revealing which credential failed.</summary>
    private const string InvalidCredentialsMessage = "Invalid credentials";

    /// <summary>Login failure text returned when a local account has not confirmed its email address.</summary>
    private const string EmailNotVerifiedMessage = "Email not verified";

    /// <summary>Fallback display name for remote profiles with no usable username.</summary>
    private const string UnknownRemoteUsername = "Unknown";

    /// <summary>Subject line used for local registration verification emails.</summary>
    private const string VerificationEmailSubject = "Confirm your registration to AutoMate!";

    /// <summary>
    ///     Registers a new user by creating their account, saving their details, and sending a
    ///     verification email.
    /// </summary>
    /// <param name="username">The username of the user being registered.</param>
    /// <param name="email">The email address of the user being registered.</param>
    /// <param name="password">The password for the user's account (will be hashed).</param>
    /// <param name="verificationLinkFactory">Factory method to generate the email verification link.</param>
    /// <param name="cancellationToken">Propagates notification that operations should be canceled.</param>
    /// <returns>
    ///     A task returning true if registration is successful; false if the email is already in
    ///     use or sending fails.
    /// </returns>
    /// <exception cref="ArgumentNullException">A required input or dependency is null.</exception>
    /// <exception cref="T:System.OperationCanceledException">
    ///     An account read or write observes cancellation.
    /// </exception>
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

        logger.LogInformation("User registration completed for user {UserId}.", newUser.Id);
        return true;
    }


    /// <summary>
    ///     Verifies a user's email using the provided token.
    /// </summary>
    /// <param name="token">The secure email verification token.</param>
    /// <param name="cancellationToken">Propagates notification that operations should be canceled.</param>
    /// <returns>
    ///     A task returning true if successful; false if token is invalid, expired, or user already
    ///     verified.
    /// </returns>
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

        logger.LogInformation("Email verification completed for user {UserId}.", user.Id);
        return true;
    }


    /// <summary>
    ///     Authenticates a user with the specified email and password.
    /// </summary>
    /// <param name="email">The email address of the user.</param>
    /// <param name="password">The password provided by the user.</param>
    /// <param name="cancellationToken">Propagates notification that operations should be canceled.</param>
    /// <returns>A task returning a tuple containing the user if successful, or an error message.</returns>
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


    /// <summary>
    ///     Creates or updates a GitHub user in the database.
    /// </summary>
    /// <param name="githubId">The unique GitHub account ID.</param>
    /// <param name="username">The username of the GitHub account.</param>
    /// <param name="email">The email associated with the GitHub account.</param>
    /// <param name="avatarUrl">The user's avatar URL.</param>
    /// <param name="accessToken">The GitHub OAuth access token.</param>
    /// <param name="cancellationToken">Propagates notification that operations should be canceled.</param>
    /// <returns>A task that completes after the GitHub account metadata is persisted.</returns>
    public async Task CreateOrUpdateGitHubUserAsync(string githubId, string username, string email, string? avatarUrl,
        string? accessToken, CancellationToken cancellationToken = default)
    {
        var profile = NormalizeGitHubProfile(githubId, username, email, avatarUrl, accessToken);

        var existingUser = await dbContext.Users
            .OfType<RemoteUser>()
            .FirstOrDefaultAsync(u => u.AccountId == profile.AccountId, cancellationToken);

        if (existingUser == null)
        {
            existingUser = CreateRemoteUser(profile);
            dbContext.Users.Add(existingUser);
        }
        else
        {
            ApplyGitHubProfile(existingUser, profile);
        }

        await dbContext.SaveChangesAsync(cancellationToken);
        logger.LogInformation("GitHub account profile saved for user {UserId}.", existingUser.Id);
    }


    /// <summary>
    ///     Links or refreshes Azure OAuth identity data on an existing AutoMate user.
    /// </summary>
    /// <param name="currentUserIdentifier">
    ///     The current authenticated user's local ID or GitHub account ID.
    /// </param>
    /// <param name="azureAccountId">The unique Microsoft Entra user account ID.</param>
    /// <param name="email">The email address returned by Microsoft identity.</param>
    /// <param name="displayName">The display name returned by Microsoft identity.</param>
    /// <param name="tenantId">The Microsoft Entra tenant ID associated with the token.</param>
    /// <param name="subscriptionId">
    ///     The Azure subscription ID selected for deployments, if already known.
    /// </param>
    /// <param name="accessToken">The Azure OAuth access token.</param>
    /// <param name="refreshToken">The Azure OAuth refresh token.</param>
    /// <param name="expiresAt">The UTC timestamp when the access token expires.</param>
    /// <param name="cancellationToken">Propagates notification that operations should be canceled.</param>
    /// <returns>A task that completes after the Azure account connection is persisted.</returns>
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

        logger.LogInformation("Azure account connection saved for user {UserId}.", user.Id);
    }


    /// <summary>
    ///     Resolves the current remote user from either the persisted user ID or the GitHub account
    ///     ID claim.
    /// </summary>
    /// <param name="identifier">Provider or user identity to resolve.</param>
    /// <param name="cancellationToken">Cancels the underlying operation when observed.</param>
    /// <returns>
    ///     A task returning the remote user projection derived from the verified inputs, or null
    ///     when no value is available.
    /// </returns>
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
    /// <param name="email">The account email address.</param>
    /// <param name="cancellationToken">Cancels the underlying operation when observed.</param>
    /// <returns>A task returning whether the documented email in use async condition holds.</returns>
    private async Task<bool> IsEmailInUseAsync(string email, CancellationToken cancellationToken)
    {
        return await dbContext.Users.AnyAsync(u => u.Email == email, cancellationToken);
    }

    /// <summary>
    ///     Creates a local user with a hashed password and verification token ready for
    ///     persistence.
    /// </summary>
    /// <param name="registration">Validated local registration input.</param>
    /// <returns>The local user projection derived from the verified inputs.</returns>
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
    ///     Sends the registration verification email and converts email delivery failures into a
    ///     false result.
    /// </summary>
    /// <param name="user">The local or connected account whose metadata is being updated.</param>
    /// <param name="verificationLinkFactory">
    ///     Builds the email-verification link from its generated token.
    /// </param>
    /// <param name="cancellationToken">Cancels the underlying operation when observed.</param>
    /// <returns>A task returning true when validation or acquisition succeeds; otherwise false.</returns>
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
    /// <param name="user">The local or connected account whose metadata is being updated.</param>
    /// <param name="cancellationToken">Cancels the underlying operation when observed.</param>
    /// <returns>A task representing completion of the asynchronous operation.</returns>
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
    /// <param name="email">The account email address.</param>
    /// <returns>The bounded normalized text with supported unsafe values removed.</returns>
    private static string NormalizeEmail(string email)
    {
        return email.Trim().ToLowerInvariant();
    }

    /// <summary>
    ///     Validates and normalizes local registration input without throwing for normal user
    ///     mistakes.
    /// </summary>
    /// <param name="username">The account user name.</param>
    /// <param name="email">The account email address.</param>
    /// <param name="password">The supplied account password; never include it in diagnostics.</param>
    /// <param name="registration">Validated local registration input.</param>
    /// <returns>True when validation or acquisition succeeds; otherwise false.</returns>
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
    /// <param name="githubId">The external GitHub account identifier.</param>
    /// <param name="username">The account user name.</param>
    /// <param name="email">The account email address.</param>
    /// <param name="avatarUrl">The optional GitHub profile image URL.</param>
    /// <param name="accessToken">The provider access token; never include it in diagnostics.</param>
    /// <returns>The git hub profile projection derived from the verified inputs.</returns>
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
    /// <param name="profile">Normalized external profile metadata.</param>
    /// <returns>The remote user projection derived from the verified inputs.</returns>
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
    /// <param name="user">The local or connected account whose metadata is being updated.</param>
    /// <param name="profile">Normalized external profile metadata.</param>
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
    /// <param name="user">The local or connected account whose metadata is being updated.</param>
    /// <param name="connection">Resolved Azure account and scoped token metadata.</param>
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
    /// <param name="user">The local or connected account whose metadata is being updated.</param>
    private static void MarkEmailVerified(LocalUser user)
    {
        user.IsEmailVerified = true;
        user.EmailVerificationToken = null;
        user.VerificationTokenExpiry = null;
    }

    /// <summary>
    ///     Refreshes a password hash when the configured hasher reports that rehashing is needed.
    /// </summary>
    /// <param name="user">The local or connected account whose metadata is being updated.</param>
    /// <param name="password">The supplied account password; never include it in diagnostics.</param>
    private void RehashPassword(LocalUser user, string password)
    {
        user.PasswordHash = passwordHasher.HashPassword(user, password);
    }

    /// <summary>
    ///     Builds the plain-text registration verification email body.
    /// </summary>
    /// <param name="verificationLink">Account verification link to send to the registered owner.</param>
    /// <returns>The verification email body derived from the validated input.</returns>
    private static string CreateVerificationEmailBody(string verificationLink)
    {
        return $"Welcome to AutoMate!\n\nPlease follow this link for verification:\n{verificationLink}";
    }

    /// <summary>
    ///     Creates a short non-reversible token fingerprint for logging failed verification
    ///     attempts.
    /// </summary>
    /// <param name="token">Scoped provider or verification token; never include it in diagnostics.</param>
    /// <returns>The protected or scoped credential value; it must never enter diagnostics.</returns>
    private static string CreateTokenFingerprint(string token)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(token));
        return Base64UrlEncode(hash[..8]);
    }

    /// <summary>
    ///     Encodes binary data using URL-safe Base64 without padding.
    /// </summary>
    /// <param name="bytes">The maximum encoded UTF-8 size of provider input.</param>
    /// <returns>The base64url encode derived from the validated input.</returns>
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
    /// <param name="Username">The account user name.</param>
    /// <param name="Email">The account email address.</param>
    /// <param name="Password">The supplied account password; never include it in diagnostics.</param>
    private readonly record struct RegistrationInput(string Username, string Email, string Password);

    /// <summary>
    ///     Normalized GitHub profile values used when creating or updating remote users.
    /// </summary>
    /// <param name="AccountId">External provider account identity.</param>
    /// <param name="Username">The account user name.</param>
    /// <param name="Email">The account email address.</param>
    /// <param name="AvatarUrl">The optional GitHub profile image URL.</param>
    /// <param name="AccessToken">The provider access token; never include it in diagnostics.</param>
    private readonly record struct GitHubProfile(
        string AccountId,
        string Username,
        string Email,
        string? AvatarUrl,
        string? AccessToken);

    /// <summary>
    ///     Azure account connection values persisted for a remote user.
    /// </summary>
    /// <param name="AccountId">External provider account identity.</param>
    /// <param name="TenantId">The internal owner-account telemetry identity.</param>
    /// <param name="SubscriptionId">The optional Azure subscription identifier.</param>
    /// <param name="AccessToken">The provider access token; never include it in diagnostics.</param>
    /// <param name="RefreshToken">The optional provider refresh token; never include it in diagnostics.</param>
    /// <param name="ExpiresAt">The expiry time of the token or stored envelope, if known.</param>
    private readonly record struct AzureAccountConnection(
        string AccountId,
        string? TenantId,
        string? SubscriptionId,
        string? AccessToken,
        string? RefreshToken,
        DateTimeOffset? ExpiresAt);
}