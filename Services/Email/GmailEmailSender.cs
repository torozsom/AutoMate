using System.Net;
using System.Net.Mail;
using Microsoft.Extensions.Configuration;

namespace Services.Email;

/// <summary>
///     An implementation of the IEmailSender interface that uses Gmail's SMTP server to send emails.
///     The sender's email, app password, and sender name are retrieved from the configuration settings.
/// </summary>
/// <param name="configuration"></param>
public class GmailEmailSender(IConfiguration configuration) : IEmailSender
{
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
        var senderEmail = configuration["Email:SenderEmail"];
        var appPassword = configuration["Email:AppPassword"];
        var senderName = configuration["Email:SenderName"] ?? "AutoMate";

        if (string.IsNullOrEmpty(senderEmail) || string.IsNullOrEmpty(appPassword))
            throw new InvalidOperationException("Email sender credentials are not configured.");

        var mailMessage = new MailMessage
        {
            From = new MailAddress(senderEmail, senderName),
            Subject = subject,
            Body = message,
            IsBodyHtml = true
        };
        mailMessage.To.Add(toEmail);

        using var smtpClient = new SmtpClient("smtp.gmail.com", 587);
        smtpClient.Credentials = new NetworkCredential(senderEmail, appPassword);
        smtpClient.EnableSsl = true;
        await smtpClient.SendMailAsync(mailMessage);
    }
}