namespace Services.Email;

/// <summary>
///     Interface for sending emails asynchronously.
/// </summary>
public interface IEmailSenderService
{
    /// <summary>
    ///     Sends an email with the specified subject and HTML message asynchronously.
    /// </summary>
    /// <param name="toEmail">The recipient's email address.</param>
    /// <param name="subject">The subject of the email.</param>
    /// <param name="message">The HTML content of the email.</param>
    /// <param name="cancellationToken">Propagates notification that operations should be canceled.</param>
    Task SendEmailAsync(string toEmail, string subject, string message, CancellationToken cancellationToken = default);
}