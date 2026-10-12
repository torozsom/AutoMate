namespace Domain.Entities;

/// <summary>
///     A local account with password-hash and email-verification metadata.
/// </summary>
public class LocalUser : User
{
    /// <summary>The hashed password for the user. This should never store the plain text password.</summary>
    public string? PasswordHash { get; set; }

    /// <summary>Whether the user's email address has been verified.</summary>
    public bool IsEmailVerified { get; set; }

    /// <summary>
    ///     The verification token sent to the user's email address. Used for confirming email
    ///     ownership.
    /// </summary>
    public string? EmailVerificationToken { get; set; }

    /// <summary>The timestamp when the email verification token expires.</summary>
    public DateTimeOffset? VerificationTokenExpiry { get; set; }
}