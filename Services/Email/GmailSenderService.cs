using MailKit.Net.Smtp;
using MailKit.Security;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MimeKit;

namespace Services.Email;

/// <summary>
///     An implementation of the IEmailSender interface that uses Gmail's SMTP server to send emails.
///     The sender's email, app password, and sender name are retrieved from the configuration settings.
/// </summary>
public class GmailSenderService(
    IOptions<EmailOptions> options,
    ILogger<GmailSenderService> logger) : IEmailSenderService
{
    private readonly EmailOptions _options = options.Value;

    /// <inheritdoc />
    public async Task SendEmailAsync(string toEmail, string subject, string message,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(toEmail))
            throw new ArgumentException("Recipient email is required.", nameof(toEmail));

        if (string.IsNullOrWhiteSpace(subject))
            throw new ArgumentException("Email subject is required.", nameof(subject));

        if (string.IsNullOrWhiteSpace(_options.SenderEmail) || string.IsNullOrWhiteSpace(_options.AppPassword))
        {
            logger.LogCritical("[GmailSenderService] Email sender credentials are not configured.");
            throw new InvalidOperationException("Email sender credentials are not configured.");
        }

        logger.LogInformation(
            "[GmailSenderService] Attempting to send email with subject '{Subject}'...", subject);

        // Create a new MIME message and set the sender, recipient, subject, and body
        var mimeMessage = new MimeMessage();
        mimeMessage.From.Add(new MailboxAddress(_options.SenderName, _options.SenderEmail));
        mimeMessage.To.Add(MailboxAddress.Parse(toEmail));
        mimeMessage.Subject = subject;

        var bodyBuilder = new BodyBuilder { TextBody = message };
        mimeMessage.Body = bodyBuilder.ToMessageBody();

        // Send the email using Gmail's SMTP server
        using var smtpClient = new SmtpClient();

        try
        {
            await smtpClient.ConnectAsync(_options.SmtpHost, _options.SmtpPort, SecureSocketOptions.StartTls,
                cancellationToken);
            await smtpClient.AuthenticateAsync(_options.SenderEmail, _options.AppPassword, cancellationToken);
            await smtpClient.SendAsync(mimeMessage, cancellationToken);
        }
        finally
        {
            if (smtpClient.IsConnected)
                await smtpClient.DisconnectAsync(true, CancellationToken.None);
        }

        logger.LogInformation("[GmailSenderService] Email successfully sent.");
    }
}
