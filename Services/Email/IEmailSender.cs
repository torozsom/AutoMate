namespace Services.Email;


/// <summary>
///     Interface for sending emails. This can be implemented using
///     various email service providers (e.g., SMTP, SendGrid, etc.).
/// </summary>
public interface IEmailSender
{
    /// <summary>
    ///     Sends an email asynchronously.
    /// </summary>
    /// <param name="toEmail">The recipient's email address.</param>
    /// <param name="subject">The subject of the email.</param>
    /// <param name="message">The body content of the email.</param>
    /// <returns>A task that represents the asynchronous operation.</returns>
    Task SendEmailAsync(string toEmail, string subject, string message);
}