using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace FamilyMemories.Stories;

public enum StoryBlockKind { Paragraph, Heading, BulletList, NumberedList, Quote, Media }

/// <summary>One block of a story: safe HTML for text blocks, or a photo/recording/video placed at this point.</summary>
public sealed record StoryBlock(StoryBlockKind Kind, string Html, IReadOnlyList<string> Items, Guid? MediaId = null, string? Caption = null)
{
    public bool IsText => Kind != StoryBlockKind.Media;
}

/// <summary>
/// Story text stays plain text, written with a few marks people already know from email (D-078):
/// <c>**bold**</c>, <c>*italic*</c>, <c>## heading</c>, <c>- list</c>, <c>1. list</c>, <c>&gt; quote</c>, <c>[words](https://…)</c>,
/// and a line <c>![caption](media:ID)</c> that places a photo, recording or video at that point.
/// Everything is HTML-encoded; only https, http and mailto links are made, always with rel="noopener".
/// </summary>
public static partial class StoryMarkup
{
    public static IReadOnlyList<StoryBlock> Parse(string? body) =>
    [
        .. RawBlocks(body).Select(b => b.Kind switch
        {
            StoryBlockKind.Media => new StoryBlock(StoryBlockKind.Media, "", [], b.MediaId, b.Caption),
            StoryBlockKind.BulletList or StoryBlockKind.NumberedList => new StoryBlock(b.Kind, "", [.. b.Lines.Select(Inline)]),
            _ => new StoryBlock(b.Kind, string.Join("<br>", b.Lines.Select(Inline)), []),
        }),
    ];

    /// <summary>Photos, recordings and videos placed in the text, in order.</summary>
    public static IReadOnlyList<Guid> PlacedMedia(string? body) =>
        [.. Parse(body).Where(b => b.MediaId is not null).Select(b => b.MediaId!.Value).Distinct()];

    /// <summary>The words without the marks: for excerpts, search snippets and emails.</summary>
    public static string PlainText(string? body)
    {
        if (string.IsNullOrWhiteSpace(body)) return "";
        var sb = new StringBuilder();
        foreach (var raw in body.Replace("\r\n", "\n").Split('\n'))
        {
            var line = raw.Trim();
            if (MediaLine().IsMatch(line)) continue;
            line = HeadingLine().Match(line) is { Success: true } h ? h.Groups[1].Value
                : Bullet().Match(line) is { Success: true } b ? b.Groups[1].Value
                : Numbered().Match(line) is { Success: true } n ? n.Groups[1].Value
                : line.StartsWith('>') ? line[1..].Trim() : line;
            line = Link().Replace(line, "$1");
            line = Bold().Replace(line, "$1");
            line = Italic().Replace(line, "$1");
            sb.Append(line).Append('\n');
        }
        return Regex.Replace(sb.ToString(), "\n{3,}", "\n\n").Trim();
    }

    /// <summary>A short excerpt of the plain words, cut at a word.</summary>
    public static string Excerpt(string? body, int max = 180)
    {
        var text = PlainText(body).Replace('\n', ' ');
        text = Regex.Replace(text, @"\s+", " ").Trim();
        if (text.Length <= max) return text;
        var cut = text.LastIndexOf(' ', max - 1);
        return text[..(cut > 0 ? cut : max)].TrimEnd(',', '.', ';', ':') + "…";
    }

    /// <summary>Markdown for pandoc (e-book and Word exports): our marks kept, anything else escaped with <paramref name="escape"/>.</summary>
    public static string InlineMarkdown(string text, Func<string, string> escape)
    {
        var sb = new StringBuilder();
        var last = 0;
        foreach (Match m in InlineToken().Matches(text))
        {
            sb.Append(escape(text[last..m.Index]));
            if (m.Groups["linktext"].Success) sb.Append('[').Append(escape(m.Groups["linktext"].Value)).Append("](").Append(m.Groups["url"].Value).Append(')');
            else if (m.Groups["bold"].Success) sb.Append("**").Append(InlineMarkdown(m.Groups["bold"].Value, escape)).Append("**");
            else sb.Append('*').Append(InlineMarkdown(m.Groups["italic"].Value, escape)).Append('*');
            last = m.Index + m.Length;
        }
        return sb.Append(escape(text[last..])).ToString();
    }

    /// <summary>A text block's raw lines, for exporters that write their own format (Markdown).</summary>
    public static IEnumerable<(StoryBlockKind Kind, IReadOnlyList<string> Lines, Guid? MediaId, string? Caption)> RawBlocks(string? body)
    {
        if (string.IsNullOrWhiteSpace(body)) yield break;
        var lines = body.Replace("\r\n", "\n").Split('\n');
        var paragraph = new List<string>();
        for (var i = 0; i < lines.Length; i++)
        {
            var t = lines[i].Trim();
            if (t.Length == 0)
            {
                if (paragraph.Count > 0) { yield return (StoryBlockKind.Paragraph, [.. paragraph], null, null); paragraph.Clear(); }
                continue;
            }
            if (MediaLine().Match(t) is { Success: true } media && Guid.TryParse(media.Groups[2].Value, out var id))
            {
                if (paragraph.Count > 0) { yield return (StoryBlockKind.Paragraph, [.. paragraph], null, null); paragraph.Clear(); }
                yield return (StoryBlockKind.Media, [], id, media.Groups[1].Value.Trim() is { Length: > 0 } c ? c : null);
                continue;
            }
            if (HeadingLine().Match(t) is { Success: true } h)
            {
                if (paragraph.Count > 0) { yield return (StoryBlockKind.Paragraph, [.. paragraph], null, null); paragraph.Clear(); }
                yield return (StoryBlockKind.Heading, [h.Groups[1].Value], null, null);
                continue;
            }
            var listKind = Bullet().IsMatch(t) ? StoryBlockKind.BulletList : Numbered().IsMatch(t) ? StoryBlockKind.NumberedList : (StoryBlockKind?)null;
            if (listKind is { } kind && paragraph.Count == 0)
            {
                var pattern = kind == StoryBlockKind.BulletList ? Bullet() : Numbered();
                var items = new List<string>();
                while (i < lines.Length && pattern.Match(lines[i].Trim()) is { Success: true } item) { items.Add(item.Groups[1].Value); i++; }
                i--;
                yield return (kind, items, null, null);
                continue;
            }
            if (t.StartsWith('>') && paragraph.Count == 0)
            {
                var quote = new List<string>();
                while (i < lines.Length && lines[i].TrimStart().StartsWith('>')) { quote.Add(lines[i].TrimStart()[1..].Trim()); i++; }
                i--;
                yield return (StoryBlockKind.Quote, quote, null, null);
                continue;
            }
            paragraph.Add(t);
        }
        if (paragraph.Count > 0) yield return (StoryBlockKind.Paragraph, [.. paragraph], null, null);
    }

    /// <summary>The line the editor inserts to place a photo, recording or video.</summary>
    public static string MediaMarker(Guid id, string? caption) =>
        $"![{(caption ?? "").Replace("]", ")").Replace("\n", " ").Trim()}](media:{id})";

    /// <summary>One line of text with bold, italic and links; everything else HTML-encoded.</summary>
    internal static string Inline(string text)
    {
        var sb = new StringBuilder();
        var last = 0;
        foreach (Match m in InlineToken().Matches(text))
        {
            sb.Append(WebUtility.HtmlEncode(text[last..m.Index]));
            if (m.Groups["linktext"].Success)
                sb.Append("<a href=\"").Append(WebUtility.HtmlEncode(m.Groups["url"].Value)).Append("\" rel=\"noopener\">")
                  .Append(WebUtility.HtmlEncode(m.Groups["linktext"].Value)).Append("</a>");
            else if (m.Groups["bold"].Success) sb.Append("<strong>").Append(Inline(m.Groups["bold"].Value)).Append("</strong>");
            else sb.Append("<em>").Append(Inline(m.Groups["italic"].Value)).Append("</em>");
            last = m.Index + m.Length;
        }
        return sb.Append(WebUtility.HtmlEncode(text[last..])).ToString();
    }

    [GeneratedRegex(@"^!\[([^\]\n]*)\]\(media:([0-9a-fA-F-]{36})\)$")]
    private static partial Regex MediaLine();
    [GeneratedRegex(@"^#{1,4}\s+(.+)$")]
    private static partial Regex HeadingLine();
    [GeneratedRegex(@"^[-*•]\s+(.+)$")]
    private static partial Regex Bullet();
    [GeneratedRegex(@"^\d{1,3}[.)]\s+(.+)$")]
    private static partial Regex Numbered();
    [GeneratedRegex(@"\[(?<linktext>[^\]\n]+)\]\((?<url>(?:https?://|mailto:)[^\s()<>""]+)\)|\*\*(?<bold>[^*\n]+?)\*\*|(?<![*\w])\*(?<italic>[^*\s](?:[^*\n]*?[^*\s])?)\*(?![*\w])")]
    private static partial Regex InlineToken();
    [GeneratedRegex(@"\[([^\]\n]+)\]\((?:https?://|mailto:)[^\s()<>""]+\)")]
    private static partial Regex Link();
    [GeneratedRegex(@"\*\*([^*\n]+?)\*\*")]
    private static partial Regex Bold();
    [GeneratedRegex(@"(?<![*\w])\*([^*\s](?:[^*\n]*?[^*\s])?)\*(?![*\w])")]
    private static partial Regex Italic();
}

/// <summary>What the story-body partial needs: the parsed blocks and the story's attachments (for placed media).</summary>
public sealed record StoryBodyView(
    IReadOnlyList<StoryBlock> Blocks,
    IReadOnlyList<FamilyMemories.Data.Attachment> Items,
    IReadOnlyDictionary<Guid, List<FamilyMemories.Data.Person>> PhotoPeople)
{
    /// <summary>The attachments not placed in the text, shown after it as before.</summary>
    public IEnumerable<FamilyMemories.Data.Attachment> Unplaced =>
        Items.Where(a => !Blocks.Any(b => b.MediaId == a.Id));
}
