namespace Core.Entities;

/// <summary>
///     The LocalUser class represents a user who has registered with the application.
/// </summary>
public class LocalUser : User
{
    /// <summary>
    /// Gets or sets the hashed password for the user.
    /// This should never store the plain text password.
    /// </summary>
    public string? PasswordHash { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the user's email address has been verified.
    /// </summary>
    public bool IsEmailVerified { get; set; }

    /// <summary>
    /// Gets or sets the verification token sent to the user's email address.
    /// Used for confirming email ownership.
    /// </summary>
    public string? EmailVerificationToken { get; set; }

    /// <summary>
    /// Gets or sets the timestamp when the email verification token expires.
    /// </summary>
    public DateTimeOffset? VerificationTokenExpiry { get; set; }
}