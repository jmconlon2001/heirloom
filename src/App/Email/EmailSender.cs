using MailKit.Net.Smtp;
using MailKit.Security;
using MimeKit;

namespace FamilyMemories.Email;

/// <param name="UnsubscribeUrl">When set, adds List-Unsubscribe and one-click List-Unsubscribe-Post headers (RFC 8058).</param>
public sealed record EmailMessage(string ToAddress, string ToName, string Subject, string TextBody, string HtmlBody, string? UnsubscribeUrl = null);

/// <summary>The transport: actually hands a message to an SMTP server. Everything else goes through <see cref="EmailService"/>.</summary>
public interface IEmailSender
{
    Task SendAsync(EmailMessage message, CancellationToken ct = default);
}

public sealed class SmtpEmailSender(SmtpSettingsStore settings, ILogger<SmtpEmailSender> log) : IEmailSender
{
    public async Task SendAsync(EmailMessage message, CancellationToken ct = default)
    {
        var smtp = await settings.GetAsync();
        if (!smtp.IsConfigured)
            throw new InvalidOperationException("Email isn't set up yet: add the SMTP server under Family admin → Email, or SMTP_HOST in .env.");

        var mime = new MimeMessage();
        mime.From.Add(new MailboxAddress(smtp.FromName, smtp.FromAddress));
        mime.To.Add(new MailboxAddress(message.ToName, message.ToAddress));
        mime.Subject = message.Subject;
        if (message.UnsubscribeUrl is not null)
        {
            mime.Headers.Add("List-Unsubscribe", $"<{message.UnsubscribeUrl}>");
            mime.Headers.Add("List-Unsubscribe-Post", "List-Unsubscribe=One-Click");
        }
        mime.Body = new BodyBuilder { TextBody = message.TextBody, HtmlBody = message.HtmlBody }.ToMessageBody();

        using var client = new SmtpClient { Timeout = 30_000 };
        var security = smtp.Security switch
        {
            Configuration.SmtpSecurity.None => SecureSocketOptions.None,
            Configuration.SmtpSecurity.SslOnConnect => SecureSocketOptions.SslOnConnect,
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
