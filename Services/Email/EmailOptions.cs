namespace Services.Email;

/// <summary>
///     Configuration options for the email sender service.
/// </summary>
public class EmailOptions
{
    public const string SectionName = "Email";

    public string SenderEmail { get; set; } = string.Empty;
    public string AppPassword { get; set; } = string.Empty;
    public string SenderName { get; set; } = "AutoMate";
    public string SmtpHost { get; set; } = "smtp.gmail.com";
    public int SmtpPort { get; set; } = 587;
}