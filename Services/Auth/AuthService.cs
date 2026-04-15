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


    /// <inheritdoc />
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
            EmailVerificationToken = Guid.NewGuid().ToString(),
            VerificationTokenExpiry = DateTimeOffset.UtcNow.AddHours(24)
        };

        newUser.PasswordHash = _passwordHasher.HashPassword(newUser, password);

        dbContext.Users.Add(newUser);
        await dbContext.SaveChangesAsync();

        var verificationLink = verificationLinkFactory(newUser.EmailVerificationToken);

        await emailSender.SendEmailAsync(
            newUser.Email,
            "Confirm your registration to AutoMate!",
            "Welcome to AutoMate!\n\nPlease follow this link for verification:\n" + verificationLink);

        return true;
    }


    /// <inheritdoc />
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


    /// <inheritdoc />
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


    /// <inheritdoc />
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
            existingUser.AvatarUrl = avatarUrl;
            existingUser.AccessToken = accessToken;
        }

        await dbContext.SaveChangesAsync();
    }
}