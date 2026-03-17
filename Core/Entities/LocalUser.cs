namespace Core.Entities;

/// <summary>
///     The LocalUser class represents a user who has registered with the application.
/// </summary>
public class LocalUser : User
{

    /// The hashed password for the user.
    public string? PasswordHash { get; set; }

    /// A flag indicating whether the user's email address has been verified.'
    public bool IsEmailVerified { get; set; }

    /// The verification token for the user's email address.'
    public string? EmailVerificationToken { get; set; }

    /// The timestamp when the verification token expires.
    public DateTimeOffset? VerificationTokenExpiry { get; set; }
}