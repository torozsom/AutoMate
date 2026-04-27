namespace Services.Email;

/// <summary>
///     Interface for sending emails. This can be implemented using
///     various email service providers (e.g., SMTP, SendGrid, etc.).
/// </summary>
public interface IEmailSenderService
{
    /// Sends an email with the specified subject and message asynchronously.
    Task SendEmailAsync(string toEmail, string subject, string message);
}