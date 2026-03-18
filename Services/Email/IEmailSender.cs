namespace Services.Email;


/// <summary>
///     Interface for sending emails. This can be implemented using
///     various email service providers (e.g., SMTP, SendGrid, etc.).
/// </summary>
public interface IEmailSender
{
    Task SendEmailAsync(string toEmail, string subject, string message);
}