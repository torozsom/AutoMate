using System.Net;
using System.Net.Mail;
using Microsoft.Extensions.Configuration;

namespace Services.Email;

public class GmailEmailSender(IConfiguration configuration) : IEmailSender
{
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