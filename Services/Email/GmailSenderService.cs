using MailKit.Net.Smtp;
using MailKit.Security;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MimeKit;

namespace Services.Email;

/// <summary>
///     Sends transactional email through an SMTP provider configured with Gmail-compatible settings.
/// </summary>
public sealed class GmailSenderService(
    IOptions<EmailOptions> options,
    ILogger<GmailSenderService> logger) : IEmailSenderService
{
    /// <summary>
    ///     Runtime SMTP options bound from configuration.
    /// </summary>
    private readonly EmailOptions _options = options.Value;

    /// <inheritdoc />
    public async Task SendEmailAsync(string toEmail, string subject, string message,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(toEmail))
            throw new ArgumentException("Recipient email is required.", nameof(toEmail));

        if (string.IsNullOrWhiteSpace(subject))
            throw new ArgumentException("Email subject is required.", nameof(subject));

        EnsureCredentialsConfigured();

        logger.LogInformation(
            "[GmailSenderService] Attempting to send email with subject '{Subject}'...", subject);

        var mimeMessage = CreateMessage(toEmail, subject, message);

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

    /// <summary>
    ///     Verifies that configured sender credentials are present before connecting to SMTP.
    /// </summary>
    private void EnsureCredentialsConfigured()
    {
        if (!string.IsNullOrWhiteSpace(_options.SenderEmail) && !string.IsNullOrWhiteSpace(_options.AppPassword))
            return;

        logger.LogCritical("[GmailSenderService] Email sender credentials are not configured.");
        throw new InvalidOperationException("Email sender credentials are not configured.");
    }

    /// <summary>
    ///     Creates the MIME message sent through SMTP.
    /// </summary>
    private MimeMessage CreateMessage(string toEmail, string subject, string message)
    {
        var mimeMessage = new MimeMessage();
        mimeMessage.From.Add(new MailboxAddress(_options.SenderName, _options.SenderEmail));
        mimeMessage.To.Add(MailboxAddress.Parse(toEmail));
        mimeMessage.Subject = subject;

        var bodyBuilder = new BodyBuilder { TextBody = message };
        mimeMessage.Body = bodyBuilder.ToMessageBody();

        return mimeMessage;
    }
}