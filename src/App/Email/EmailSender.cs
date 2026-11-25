using FamilyMemories.Configuration;
using MailKit.Net.Smtp;
using MailKit.Security;
using MimeKit;

namespace FamilyMemories.Email;

public sealed record EmailMessage(string ToAddress, string ToName, string Subject, string TextBody, string HtmlBody);

public interface IEmailSender
{
    Task SendAsync(EmailMessage message, CancellationToken ct = default);
}

/// <summary>
/// Plain SMTP via MailKit. Phase 4 moves sending onto Hangfire with retries and an EmailLog table;
/// for now we send inline and log failures.
/// </summary>
public sealed class SmtpEmailSender(AppOptions options, ILogger<SmtpEmailSender> log) : IEmailSender
{
    public async Task SendAsync(EmailMessage message, CancellationToken ct = default)
    {
        var smtp = options.Smtp;
        if (!smtp.IsConfigured)
        {
            log.LogWarning("SMTP is not configured (SMTP_HOST is empty); email to {To} was not sent", message.ToAddress);
            return;
        }

        var mime = new MimeMessage();
        mime.From.Add(new MailboxAddress(smtp.FromName, smtp.FromAddress));
        mime.To.Add(new MailboxAddress(message.ToName, message.ToAddress));
        mime.Subject = message.Subject;
        mime.Body = new BodyBuilder { TextBody = message.TextBody, HtmlBody = message.HtmlBody }.ToMessageBody();

        using var client = new SmtpClient();
        var security = smtp.Security switch
        {
            SmtpSecurity.None => SecureSocketOptions.None,
            SmtpSecurity.SslOnConnect => SecureSocketOptions.SslOnConnect,
            _ => SecureSocketOptions.StartTls,
        };
        await client.ConnectAsync(smtp.Host, smtp.Port, security, ct);
        if (smtp.Username is not null)
            await client.AuthenticateAsync(smtp.Username, smtp.Password ?? "", ct);
        await client.SendAsync(mime, ct);
        await client.DisconnectAsync(quit: true, ct);

        log.LogInformation("Sent email {Subject} to {To}", message.Subject, message.ToAddress);
    }
}
