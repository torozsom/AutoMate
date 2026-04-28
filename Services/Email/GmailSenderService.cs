using System.Net;
using System.Net.Mail;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Services.Email;

/// <summary>
///     An implementation of the IEmailSender interface that uses Gmail's SMTP server to send emails.
///     The sender's email, app password, and sender name are retrieved from the configuration settings.
/// </summary>
public class GmailSenderService : IEmailSenderService
{
    private const string SmtpHost = "smtp.gmail.com";
    private const int SmtpPort = 587;
    private readonly string _appPassword;
    private readonly ILogger<GmailSenderService> _logger;
    private readonly string _senderEmail;
    private readonly string _senderName;


    /// <summary>
    ///     Initializes a new instance of the GmailSenderService class with the specified configuration and logger.
    ///     The constructor retrieves the sender's email, app password, and sender name from the configuration settings.
    /// </summary>
    /// <param name="configuration">The configuration to be parsed for credentials.</param>
    /// <param name="logger">A logger.</param>
    /// <exception cref="InvalidOperationException"></exception>
    public GmailSenderService(IConfiguration configuration, ILogger<GmailSenderService> logger)
    {
        _logger = logger;

        _senderEmail = configuration["Email:SenderEmail"] ?? string.Empty;
        _appPassword = configuration["Email:AppPassword"] ?? string.Empty;
        _senderName = configuration["Email:SenderName"] ?? "AutoMate";

        if (string.IsNullOrWhiteSpace(_senderEmail) || string.IsNullOrWhiteSpace(_appPassword))
        {
            _logger.LogCritical("[GmailSender] Email sender credentials are not configured!");
            throw new InvalidOperationException("Email sender credentials are not configured.");
        }
    }

    /// <summary>
    ///     Sends an email using Gmail's SMTP server. The sender's email,
    ///     app password, and sender name are retrieved from the configuration.
    /// </summary>
    /// <param name="toEmail">The recipient's email address.</param>
    /// <param name="subject">The subject of the email to be sent.</param>
    /// <param name="message">The message of the email to be sent.</param>
    /// <exception cref="InvalidOperationException">Throws if email sender credentials are not configured.</exception>
    public async Task SendEmailAsync(string toEmail, string subject, string message)
    {
        try
        {
            _logger.LogInformation(
                "[GmailSenderService] Attempting to send email with subject '{Subject}'...",
                subject);

            using var mailMessage = new MailMessage();
            mailMessage.From = new MailAddress(_senderEmail, _senderName);
            mailMessage.Subject = subject;
            mailMessage.Body = message;
            mailMessage.IsBodyHtml = true;
            mailMessage.To.Add(toEmail);

            using var smtpClient = new SmtpClient(SmtpHost, SmtpPort);
            smtpClient.Credentials = new NetworkCredential(_senderEmail, _appPassword);
            smtpClient.EnableSsl = true;

            await smtpClient.SendMailAsync(mailMessage);

            _logger.LogInformation(
                "[GmailSenderService] Email successfully sent."
            );
        }
        catch (SmtpException smtpEx)
        {
            _logger.LogError(smtpEx,
                "[GmailSenderService] SMTP error occurred while sending email. StatusCode: {StatusCode}",
                smtpEx.StatusCode);
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "[GmailSenderService] An unexpected error occurred while sending email."
            );
            throw;
        }
    }
}