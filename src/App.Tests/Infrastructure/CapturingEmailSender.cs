using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using FamilyMemories.Email;

namespace FamilyMemories.Tests.Infrastructure;

public sealed partial class CapturingEmailSender : IEmailSender
{
    public ConcurrentQueue<EmailMessage> Sent { get; } = new();

    public Task SendAsync(EmailMessage message, CancellationToken ct = default)
    {
        Sent.Enqueue(message);
        return Task.CompletedTask;
    }

    public IReadOnlyList<EmailMessage> To(string address) =>
        Sent.Where(m => string.Equals(m.ToAddress, address, StringComparison.OrdinalIgnoreCase)).ToList();

    public static Uri ExtractMagicLink(EmailMessage message) =>
        new(MagicLinkRegex().Match(message.TextBody).Value);

    [GeneratedRegex(@"https?://\S+/account/magic-link\?token=[A-Za-z0-9_\-%]+")]
    private static partial Regex MagicLinkRegex();
}
