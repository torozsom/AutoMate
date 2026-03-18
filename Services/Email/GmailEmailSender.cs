using Microsoft.Extensions.Configuration;
using System.Net;
using System.Net.Mail;

namespace Services.Email;

public class GmailEmailSender(IConfiguration configuration) : IEmailSender
{
    public async Task SendEmailAsync(string toEmail, string subject, string message)
    {
        string? senderEmail = configuration["Email:SenderEmail"];
        string? appPassword = configuration["Email:AppPassword"];
        string senderName = configuration["Email:SenderName"] ?? "AutoMate";

        if (string.IsNullOrEmpty(senderEmail) || string.IsNullOrEmpty(appPassword))
        {
            throw new InvalidOperationException("Email sender credentials are not configured.");
        }

        var mailMessage = new MailMessage
        {
            From = new MailAddress(senderEmail, senderName),
            Subject = subject,
            Body = message,
            IsBodyHtml = true
        };
        mailMessage.To.Add(toEmail);

        using var smtpClient = new SmtpClient("smtp.gmail.com", 587)
        {
            Credentials = new NetworkCredential(senderEmail, appPassword),
            EnableSsl = true
        };
        await smtpClient.SendMailAsync(mailMessage);
    }
}