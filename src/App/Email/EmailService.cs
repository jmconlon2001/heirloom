using FamilyMemories.Configuration;
using FamilyMemories.Data;
using Hangfire;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;

namespace FamilyMemories.Email;

public sealed record Recipient(string Address, string Name, Guid? UserId);

/// <summary>
/// The one way the app sends email: render the template, record it in <see cref="EmailLog"/>,
/// then hand it to the dispatcher (Hangfire with retries in production; immediately in tests).
/// </summary>
public sealed class EmailService(AppDbContext db, EmailTemplates templates, IEmailDispatcher dispatcher, EmailLinks links, TimeProvider clock)
{
    /// <summary>Kinds where a one-click unsubscribe header makes sense (anything not asked for directly).</summary>
    private static readonly HashSet<EmailKind> BulkKinds = [EmailKind.Reminder, EmailKind.Digest, EmailKind.ReadSummary];

    public async Task<EmailLog> SendAsync(EmailKind kind, Recipient to, IDictionary<string, object?> model, EmailTemplate? overrideTemplate = null, string? replyTo = null)
    {
        var prefs = to.UserId is { } uid ? links.PreferencesUrl(uid) : null;
        var rendered = await templates.RenderAsync(kind, to.Name, model, prefs, overrideTemplate);
        var log = new EmailLog
        {
            Id = Guid.CreateVersion7(),
            Kind = kind,
            UserId = to.UserId,
            ToAddress = to.Address,
            ToName = to.Name,
            Subject = rendered.Subject.Length > 500 ? rendered.Subject[..500] : rendered.Subject,
            TextBody = rendered.TextBody,
            HtmlBody = rendered.HtmlBody,
            UnsubscribeUrl = to.UserId is { } id && BulkKinds.Contains(kind) ? links.OneClickUnsubscribeUrl(id, kind) : null,
            ReplyTo = replyTo,
            Status = EmailStatus.Queued,
            CreatedAt = clock.GetUtcNow().UtcDateTime,
        };
        db.EmailLog.Add(log);
        await db.SaveChangesAsync();
        await dispatcher.DispatchAsync(log.Id);
        return log;
    }
}

public interface IEmailDispatcher
{
    Task DispatchAsync(Guid emailLogId);
}

/// <summary>Sends one logged email. Throws on failure so Hangfire retries (5 tries over about 4 hours).</summary>
public sealed class EmailDelivery(AppDbContext db, IEmailSender sender, TimeProvider clock, ILogger<EmailDelivery> log)
{
    public const int MaxAttempts = 6;

    [AutomaticRetry(Attempts = MaxAttempts - 1, DelaysInSeconds = [60, 300, 900, 3600, 10800], OnAttemptsExceeded = AttemptsExceededAction.Fail)]
    public async Task DeliverAsync(Guid emailLogId)
    {
        if (!await TryDeliverAsync(emailLogId)) throw new InvalidOperationException($"Email {emailLogId} could not be sent yet");
    }

    /// <summary>Returns false if sending failed (the error is recorded).</summary>
    public async Task<bool> TryDeliverAsync(Guid emailLogId)
    {
        var entry = await db.EmailLog.SingleOrDefaultAsync(e => e.Id == emailLogId);
        if (entry is null || entry.Status == EmailStatus.Sent) return true;
        entry.Attempts++;
        try
        {
            await sender.SendAsync(new EmailMessage(entry.ToAddress, entry.ToName, entry.Subject, entry.TextBody, entry.HtmlBody, entry.UnsubscribeUrl, entry.ReplyTo));
            entry.Status = EmailStatus.Sent;
            entry.SentAt = clock.GetUtcNow().UtcDateTime;
            entry.Error = null;
            await db.SaveChangesAsync();
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log.LogWarning(ex, "Sending email {EmailId} failed (attempt {Attempt})", emailLogId, entry.Attempts);
            entry.Error = ex.Message.Length > 2000 ? ex.Message[..2000] : ex.Message;
            if (entry.Attempts >= MaxAttempts) entry.Status = EmailStatus.Failed;
            await db.SaveChangesAsync();
            return false;
        }
    }
}

public sealed class HangfireEmailDispatcher(IBackgroundJobClient jobs) : IEmailDispatcher
{
    public Task DispatchAsync(Guid emailLogId)
    {
        jobs.Enqueue<EmailDelivery>(d => d.DeliverAsync(emailLogId));
        return Task.CompletedTask;
    }
}

/// <summary>Sends straight away (tests and JOBS_ENABLED=false). A failure is marked Failed, not retried.</summary>
public sealed class InlineEmailDispatcher(IServiceScopeFactory scopes) : IEmailDispatcher
{
    public async Task DispatchAsync(Guid emailLogId)
    {
        await using var scope = scopes.CreateAsyncScope();
        var delivery = scope.ServiceProvider.GetRequiredService<EmailDelivery>();
        if (!await delivery.TryDeliverAsync(emailLogId))
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            await db.EmailLog.Where(e => e.Id == emailLogId).ExecuteUpdateAsync(s => s.SetProperty(e => e.Status, EmailStatus.Failed));
        }
    }
}

/// <summary>Signed, long-lived links for email settings, so nobody has to sign in to unsubscribe.</summary>
public sealed class EmailLinks(IDataProtectionProvider protection, AppOptions options)
{
    private readonly IDataProtector protector = protection.CreateProtector("email-preferences");

    public string Token(Guid userId) => protector.Protect(userId.ToString());

    public Guid? UserFromToken(string? token)
    {
        if (string.IsNullOrEmpty(token)) return null;
        try { return Guid.TryParse(protector.Unprotect(token), out var id) ? id : null; }
        catch (System.Security.Cryptography.CryptographicException) { return null; }
    }

    public string PreferencesUrl(Guid userId) => options.BuildUrl($"email/preferences/{Token(userId)}").ToString();

    public string OneClickUnsubscribeUrl(Guid userId, EmailKind kind) =>
        options.BuildUrl($"email/unsubscribe/{Token(userId)}?list={kind.ToString().ToLowerInvariant()}").ToString();
}
