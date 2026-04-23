using System.Security.Cryptography;
using Core.Entities;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Services.Data;
using Services.Email;

namespace Services.Auth;

/// <summary>
///     Service implementation for handling user authentication, registration, and email verification.
/// </summary>
public class AuthService(AutoMateDbContext dbContext, IEmailSender emailSender) : IAuthService
{
    private readonly PasswordHasher<LocalUser> _passwordHasher = new();


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
        var emailExists = await dbContext.Users.AnyAsync(u => u.Email == email);
        if (emailExists)
            return false;

        var newUser = new LocalUser
        {
            Email = email,
            Username = username,
            IsEmailVerified = false,
            EmailVerificationToken = GenerateSecureToken(),
            VerificationTokenExpiry = DateTimeOffset.UtcNow.AddHours(24)
        };

        newUser.PasswordHash = _passwordHasher.HashPassword(newUser, password);

        dbContext.Users.Add(newUser);
        await dbContext.SaveChangesAsync();

        var verificationLink = verificationLinkFactory(newUser.EmailVerificationToken);

        try
        {
            await emailSender.SendEmailAsync(
                newUser.Email,
                "Confirm your registration to AutoMate!",
                "Welcome to AutoMate!\n\nPlease follow this link for verification:\n" + verificationLink);
        }
        catch (Exception ex)
        {
            Console.WriteLine("An error occured while trying to send the verification email! Error: " + ex.Message);
            dbContext.Users.Remove(newUser);
            await dbContext.SaveChangesAsync();
            return false;
        }

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

        if (user == null || user.IsEmailVerified || user.VerificationTokenExpiry < DateTimeOffset.UtcNow) return false;

        user.IsEmailVerified = true;
        user.EmailVerificationToken = null;
        user.VerificationTokenExpiry = null;

        await dbContext.SaveChangesAsync();
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


        var verificationResult = _passwordHasher.VerifyHashedPassword(user, user.PasswordHash!, password);

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
        }
        else
        {
            existingUser.Username = username;
            existingUser.Email = email;
            existingUser.AvatarUrl = avatarUrl;
            existingUser.AccessToken = accessToken;
        }

        await dbContext.SaveChangesAsync();
    }


    /// <summary>
    ///     Generates a secure random token for email verification purposes. The token is created
    ///     using a cryptographically secure random number generator and is encoded in a URL-safe
    ///     Base64 format, ensuring it can be safely included in email verification links without
    ///     issues related to special characters.
    /// </summary>
    /// <returns></returns>
    private static string GenerateSecureToken()
    {
        var tokenBytes = RandomNumberGenerator.GetBytes(32);
        return Convert.ToBase64String(tokenBytes)
            .Replace("+", "-")
            .Replace("/", "_")
            .TrimEnd('=');
    }
}