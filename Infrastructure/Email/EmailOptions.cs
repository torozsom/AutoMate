namespace Infrastructure.Email;

/// <summary>
///     Configuration options for the email sender service.
/// </summary>
public class EmailOptions
{
    /// <summary>Configuration section name used when binding email options.</summary>
    public const string SectionName = "Email";

    /// <summary>Email address used as the SMTP sender and authentication username.</summary>
    public string SenderEmail { get; init; } = string.Empty;

    /// <summary>SMTP app password or provider-specific credential used for authentication.</summary>
    public string AppPassword { get; init; } = string.Empty;

    /// <summary>Display name shown as the sender in outgoing email.</summary>
    public string SenderName { get; init; } = "AutoMate";

    /// <summary>SMTP host used by the configured mail provider.</summary>
    public string SmtpHost { get; init; } = "smtp.gmail.com";

    /// <summary>SMTP port used for StartTLS delivery.</summary>
    public int SmtpPort { get; init; } = 587;
}
