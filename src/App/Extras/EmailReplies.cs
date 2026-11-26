using System.Security.Cryptography;
using System.Text.RegularExpressions;
using FamilyMemories.Admin;
using FamilyMemories.Configuration;
using FamilyMemories.Data;
using FamilyMemories.Media;
using FamilyMemories.Stories;
using MailKit;
using MailKit.Net.Imap;
using MailKit.Search;
using MimeKit;
using Microsoft.EntityFrameworkCore;

namespace FamilyMemories.Extras;

public sealed record InboundEmail(string Id, string From, IReadOnlyList<string> To, string Subject, string Text, IReadOnlyList<(string Name, byte[] Bytes)> Attachments);

/// <summary>Where replies arrive. Production: an IMAP mailbox.</summary>
public interface IReplyInbox
{
    Task<IReadOnlyList<InboundEmail>> FetchUnreadAsync(CancellationToken ct);
    Task MarkDoneAsync(string id, CancellationToken ct);
}

public sealed class ImapReplyInbox(AppOptions options) : IReplyInbox
{
    private readonly Dictionary<string, UniqueId> uids = [];

    public async Task<IReadOnlyList<InboundEmail>> FetchUnreadAsync(CancellationToken ct)
    {
        using var client = await ConnectAsync(ct);
        var folder = await client.GetFolderAsync(options.ImapFolder, ct);
        await folder.OpenAsync(FolderAccess.ReadOnly, ct);
        var result = new List<InboundEmail>();
        foreach (var uid in (await folder.SearchAsync(SearchQuery.NotSeen, ct)).Take(50))
        {
            var message = await folder.GetMessageAsync(uid, ct);
            var id = uid.ToString();
            uids[id] = uid;
            result.Add(EmailReplyParser.FromMime(id, message));
        }
        await client.DisconnectAsync(true, ct);
        return result;
    }

    public async Task MarkDoneAsync(string id, CancellationToken ct)
    {
        if (!uids.TryGetValue(id, out var uid)) return;
        using var client = await ConnectAsync(ct);
        var folder = await client.GetFolderAsync(options.ImapFolder, ct);
        await folder.OpenAsync(FolderAccess.ReadWrite, ct);
        await folder.AddFlagsAsync(uid, MessageFlags.Seen, silent: true, ct);
        await client.DisconnectAsync(true, ct);
    }

    private async Task<ImapClient> ConnectAsync(CancellationToken ct)
    {
        var client = new ImapClient { Timeout = 60_000 };
        await client.ConnectAsync(options.ImapHost, options.ImapPort, options.ImapSsl, ct);
        await client.AuthenticateAsync(options.ImapUsername ?? "", options.ImapPassword ?? "", ct);
        return client;
    }
}

/// <summary>Reading a reply: who it's from, which question, what they wrote (without the quoted reminder).</summary>
public static partial class EmailReplyParser
{
    public static InboundEmail FromMime(string id, MimeMessage m)
    {
        var to = m.To.Mailboxes.Concat(m.Cc.Mailboxes).Select(x => x.Address)
            .Concat(m.Headers.Where(h => h.Field is "Delivered-To" or "X-Original-To").Select(h => h.Value.Trim())).ToList();
        var text = m.TextBody ?? HtmlToText(m.HtmlBody ?? "");
        var attachments = new List<(string, byte[])>();
        foreach (var part in m.Attachments.OfType<MimePart>())
        {
            if (part.Content is null) continue;
            using var ms = new MemoryStream();
            part.Content.DecodeTo(ms);
            attachments.Add((part.FileName ?? "attachment", ms.ToArray()));
        }
        return new InboundEmail(id, m.From.Mailboxes.FirstOrDefault()?.Address ?? "", to, m.Subject ?? "", text, attachments);
    }

    /// <summary>memories+AB12CD34@example.com → AB12CD34; also "Ref: AB12CD34" in the text, if the plus part was lost.</summary>
    public static string? FindCode(InboundEmail email)
    {
        foreach (var address in email.To)
            if (PlusRegex().Match(address) is { Success: true } m) return m.Groups[1].Value.ToUpperInvariant();
        return RefRegex().Match(email.Subject + "\n" + email.Text) is { Success: true } r ? r.Groups[1].Value.ToUpperInvariant() : null;
    }

    /// <summary>Keeps what the person wrote: drops the quoted reminder, "On … wrote:", forwarded headers and signatures.</summary>
    public static string NewText(string text)
    {
        var kept = new List<string>();
        foreach (var raw in text.Replace("\r\n", "\n").Split('\n'))
        {
            var line = raw.TrimEnd();
            if (WroteRegex().IsMatch(line) || line.StartsWith("-----Original Message", StringComparison.OrdinalIgnoreCase)
                || line == "-- " || line == "--" || line.StartsWith("Sent from my", StringComparison.OrdinalIgnoreCase)
                || line.StartsWith("From:", StringComparison.Ordinal) && kept.Count > 0)
                break;
            if (line.StartsWith('>')) continue;
            kept.Add(line);
        }
        return string.Join('\n', kept).Trim();
    }

    private static string HtmlToText(string html) =>
        System.Net.WebUtility.HtmlDecode(TagRegex().Replace(BreakRegex().Replace(html, "\n"), ""));

    [GeneratedRegex(@"\+([A-Za-z0-9]{8})@")] private static partial Regex PlusRegex();
    [GeneratedRegex(@"Ref:\s*([A-Za-z0-9]{8})\b")] private static partial Regex RefRegex();
    [GeneratedRegex(@"^On .+wrote:\s*$|^Le .+a écrit\s*:")] private static partial Regex WroteRegex();
    [GeneratedRegex("<[^>]+>")] private static partial Regex TagRegex();
    [GeneratedRegex(@"<br\s*/?>|</p>|</div>", RegexOptions.IgnoreCase)] private static partial Regex BreakRegex();
}

public sealed record ReplyResult(int Added, int Ignored);

/// <summary>Every 5 minutes (when FEATURE_EMAIL_REPLIES is on): replies become drafts for the storyteller or an admin to check and finish.</summary>
public sealed class EmailReplyProcessor(
    AppDbContext db, IReplyInbox inbox, MemoryService memories, AttachmentService attachments,
    AuditLog audit, AppOptions options, TimeProvider clock, ILogger<EmailReplyProcessor> log)
{
    private const string Alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";

    /// <summary>The Reply-To for a reminder about this question: memories+CODE@example.com.</summary>
    public async Task<string?> ReplyAddressAsync(Guid userId, Guid promptId)
    {
        if (!options.EmailRepliesEnabled || options.ReplyAddress is not { } address || !address.Contains('@')) return null;
        var existing = await db.ReplyCodes.Where(c => c.UserId == userId && c.PromptId == promptId).Select(c => c.Code).FirstOrDefaultAsync();
        var code = existing ?? new string(RandomNumberGenerator.GetItems<char>(Alphabet, 8));
        if (existing is null)
        {
            db.ReplyCodes.Add(new ReplyCode { Code = code, UserId = userId, PromptId = promptId, CreatedAt = clock.GetUtcNow().UtcDateTime });
            await db.SaveChangesAsync();
        }
        var at = address.IndexOf('@');
        return $"{address[..at]}+{code}{address[at..]}";
    }

    [Hangfire.AutomaticRetry(Attempts = 0)]
    [Hangfire.DisableConcurrentExecution(timeoutInSeconds: 60)]
    public async Task<ReplyResult> PollAsync(CancellationToken ct)
    {
        int added = 0, ignored = 0;
        foreach (var email in await inbox.FetchUnreadAsync(ct))
        {
            if (await HandleAsync(email)) added++; else ignored++;
            await inbox.MarkDoneAsync(email.Id, ct);
        }
        return new ReplyResult(added, ignored);
    }

    public async Task<bool> HandleAsync(InboundEmail email)
    {
        if (EmailReplyParser.FindCode(email) is not { } code) { log.LogInformation("Reply without a code ignored"); return false; }
        var link = await db.ReplyCodes.SingleOrDefaultAsync(c => c.Code == code);
        if (link is null) { log.LogInformation("Reply with unknown code ignored"); return false; }
        var user = await db.Users.SingleAsync(u => u.Id == link.UserId);
        // Only the person the reminder went to can add to their story this way.
        if (!string.Equals(user.Email, email.From.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            log.LogWarning("Reply for {UserId} came from a different address and was ignored", user.Id);
            audit.Add("email.reply-rejected", $"An email reply for {user.DisplayName} came from another address and was ignored", "user", user.Id);
            await db.SaveChangesAsync();
            return false;
        }

        var prompt = await db.Prompts.SingleAsync(p => p.Id == link.PromptId);
        var memory = await memories.GetOrCreateForPromptAsync(user.Id, prompt, user.Id);
        var words = EmailReplyParser.NewText(email.Text);
        if (words.Length > 0)
        {
            var current = await db.Memories.AsNoTracking().Where(m => m.Id == memory.Id).Select(m => new { m.Title, m.Body, m.Place, m.WhenYear, m.WhenMonth, m.WhenDay, m.WhenCirca, m.WhenDecade, m.WhenLabel }).SingleAsync();
            var body = string.IsNullOrWhiteSpace(current.Body) ? words : current.Body.TrimEnd() + "\n\n" + words;
            await memories.SaveAsync(memory.Id, new MemoryEdit(current.Title, body,
                new FuzzyDate(current.WhenYear, current.WhenMonth, current.WhenDay, current.WhenCirca, current.WhenDecade, current.WhenLabel), current.Place),
                user.Id, VersionKind.Save);
        }
        var photos = 0;
        foreach (var (name, bytes) in email.Attachments)
        {
            using var stream = new MemoryStream(bytes);
            var (a, _) = await attachments.AddFromStreamAsync(memory.Id, name, stream, user.Id);
            if (a is not null) photos++;
        }
        // The whole reply, as it arrived, stays next to the story as raw material (D-079).
        FamilyMemories.Stories.RawMaterial.Keep(db, memory.Id, SourceKind.EmailReply, email.Text, user.Id, clock.GetUtcNow().UtcDateTime);
        await db.Memories.Where(m => m.Id == memory.Id).ExecuteUpdateAsync(s => s
            .SetProperty(m => m.EmailReplyAt, clock.GetUtcNow().UtcDateTime)
            .SetProperty(m => m.Status, MemoryStatus.Draft));
        audit.Add("memory.email-reply", $"{user.DisplayName} answered \"{prompt.Title}\" by email ({words.Length} characters, {photos} files)", "memory", memory.Id);
        await db.SaveChangesAsync();
        return true;
    }
}
