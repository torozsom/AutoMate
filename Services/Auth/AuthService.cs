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
    /// <summary>
    ///     Registers a new user by creating their account, saving their details in the database, and sending a verification
    ///     email.
    /// </summary>
    /// <param name="username">The username of the user being registered.</param>
    /// <param name="email">The email address of the user being registered.</param>
    /// <param name="password">The password for the user's account, which will be hashed before saving.</param>
    /// <param name="verificationLinkFactory">
    ///     A factory method to generate the email verification link using the token
    ///     provided.
    /// </param>
    /// <returns>
    ///     A task that represents the asynchronous operation. Returns true if the registration is successful, or false if
    ///     the email address is already in use.
    /// </returns>
    public async Task<bool> RegisterAsync(string username, string email, string password,
        Func<string, string> verificationLinkFactory)
    {
        if (await IsEmailInUseAsync(email))
        {
            logger.LogWarning(
                "[AuthService] Registration failed: email is already in use for username '{Username}'.",
                username
            );
            return false;
        }

        var newUser = CreateLocalUserEntity(username, email, password);

        dbContext.Users.Add(newUser);
        await dbContext.SaveChangesAsync();

        var isEmailSent = await TrySendVerificationEmailAsync(newUser, verificationLinkFactory);
        if (!isEmailSent)
        {
            await RollbackUserCreationAsync(newUser);
            return false;
        }

        logger.LogInformation(
            "[AuthService] Successfully registered new user '{Username}'.",
            username
        );

        return true;
    }


    /// <summary>
    ///     Verifies the user's email address by validating the provided email verification token
    ///     and updating the user's record if the token is valid and not expired.
    /// </summary>
    /// <param name="token">The email verification token sent to the user.</param>
    /// <returns>
    ///     A task that represents the asynchronous operation. Returns true if the email verification is successful, or
    ///     false if the token is invalid, expired, or the user is already verified.
    /// </returns>
    public async Task<bool> VerifyEmailAsync(string token)
    {
        var user = await dbContext.Users
            .OfType<LocalUser>()
            .FirstOrDefaultAsync(u => u.EmailVerificationToken == token);

        if (user == null || user.IsEmailVerified || user.VerificationTokenExpiry < DateTimeOffset.UtcNow)
        {
            var sanitizedTokenForLog = token.Replace("\r", string.Empty).Replace("\n", string.Empty);
            logger.LogWarning("[AuthService] Email verification failed for token '{Token}'.", sanitizedTokenForLog);
            return false;
        }

        user.IsEmailVerified = true;
        user.EmailVerificationToken = null;
        user.VerificationTokenExpiry = null;

        await dbContext.SaveChangesAsync();
        logger.LogInformation("[AuthService] Email verified successfully for user '{Email}'.",
            MaskEmailForLogging(user.Email));

        return true;
    }


    /// <summary>
    ///     Authenticates a user by verifying their credentials and email verification status.
    /// </summary>
    /// <param name="email">The email address of the user attempting to log in.</param>
    /// <param name="password">The password provided by the user for authentication.</param>
    /// <returns>
    ///     A task representing the asynchronous operation. Returns a tuple containing the authenticated user as
    ///     <see cref="LocalUser" /> if successful, or null with an error message if authentication fails.
    /// </returns>
    public async Task<(LocalUser? User, string? ErrorMessage)> LoginAsync(string email, string password)
    {
        var user = await dbContext.Users
            .OfType<LocalUser>()
            .FirstOrDefaultAsync(u => u.Email == email);

        if (user == null)
            return (null, "Invalid credentials");

        var verificationResult = passwordHasher.VerifyHashedPassword(user, user.PasswordHash!, password);

        if (verificationResult == PasswordVerificationResult.Failed)
            return (null, "Invalid credentials");

        if (!user.IsEmailVerified)
            return (null, "Email not verified");

        return (user, null);
    }


    /// <summary>
    ///     Creates a new GitHub user account or updates an existing one in the database.
    /// </summary>
    /// <param name="githubId">The unique identifier for the user provided by GitHub.</param>
    /// <param name="username">The username of the GitHub account.</param>
    /// <param name="email">The email address associated with the GitHub account.</param>
    /// <param name="avatarUrl">The URL of the GitHub user's avatar image, if available.</param>
    /// <param name="accessToken">The OAuth access token provided for accessing GitHub APIs on behalf of the user.</param>
    /// <returns>A task that represents the asynchronous operation.</returns>
    public async Task CreateOrUpdateGitHubUserAsync(string githubId, string username, string email, string? avatarUrl,
        string? accessToken)
    {
        var existingUser = await dbContext.Users
            .OfType<GitHubUser>()
            .FirstOrDefaultAsync(u => u.AccountId == githubId);

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

        await dbContext.SaveChangesAsync();
    }


    /// <summary>
    ///     Checks if the provided email address is already associated with an existing user account in the database.
    /// </summary>
    /// <param name="email">The email to be checked if it is used already.</param>
    /// <returns></returns>
    private async Task<bool> IsEmailInUseAsync(string email)
    {
        return await dbContext.Users.AnyAsync(u => u.Email == email);
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
    ///     Attempts to send a verification email to the user with the provided email verification token. If the email fails to
    ///     send,
    ///     the method logs the error and returns false, allowing the caller to handle the failure appropriately.
    /// </summary>
    /// <param name="user">The user to be verified.</param>
    /// <param name="verificationLinkFactory">The functor that handles the link creation.</param>
    /// <returns></returns>
    private async Task<bool> TrySendVerificationEmailAsync(LocalUser user, Func<string, string> verificationLinkFactory)
    {
        var verificationLink = verificationLinkFactory(user.EmailVerificationToken!);

        try
        {
            await emailSenderService.SendEmailAsync(
                user.Email,
                "Confirm your registration to AutoMate!",
                $"Welcome to AutoMate!\n\nPlease follow this link for verification:\n{verificationLink}");

            return true;
        }
        catch (Exception ex)
        {
            logger.LogError(
                ex,
                "[AuthService] Failed to send verification email to '{Email}'.",
                MaskEmailForLogging(user.Email));
            return false;
        }
    }


    /// <summary>
    ///     Rolls back the user creation process by removing the user from the database if the email sending fails. This
    ///     ensures that
    ///     the database remains consistent and does not contain unverified user accounts that could not receive the
    ///     verification email.
    /// </summary>
    /// <param name="user">The user to be removed.</param>
    private async Task RollbackUserCreationAsync(LocalUser user)
    {
        try
        {
            dbContext.Users.Remove(user);
            await dbContext.SaveChangesAsync();
            logger.LogInformation(
                "[AuthService] Rolled back user creation for '{Email}' due to email sending failure.",
                MaskEmailForLogging(user.Email));
        }
        catch (Exception rollbackEx)
        {
            logger.LogCritical(
                rollbackEx,
                "[AuthService] CRITICAL: Failed to rollback user creation for '{Email}'. " +
                "Database might be in an inconsistent state.",
                MaskEmailForLogging(user.Email));
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


    /// <summary>
    ///     Masks an email address for logging purposes by obscuring the local part
    ///     and domain while retaining the general structure of the email.
    /// </summary>
    /// <param name="email">The email address to be masked.</param>
    /// <returns>The masked email address.</returns>
    public static string MaskEmailForLogging(string email)
    {
        if (string.IsNullOrWhiteSpace(email))
            return "***";

        var atIndex = email.IndexOf('@');
        if (atIndex <= 0 || atIndex == email.Length - 1)
            return "***";

        var local = email[..atIndex];
        var domain = email[(atIndex + 1)..];
        var maskedLocal = local.Length switch
        {
            1 => "*",
            2 => $"{local[0]}*",
            _ => $"{local[0]}***{local[^1]}"
        };

        var dotIndex = domain.LastIndexOf('.');
        if (dotIndex <= 0 || dotIndex == domain.Length - 1)
            return $"{maskedLocal}@***";

        var domainName = domain[..dotIndex];
        var tld = domain[dotIndex..];
        var maskedDomain = domainName.Length switch
        {
            1 => "*",
            2 => $"{domainName[0]}*",
            _ => $"{domainName[0]}***{domainName[^1]}"
        };

        return $"{maskedLocal}@{maskedDomain}{tld}";
    }
}