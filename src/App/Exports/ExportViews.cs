using System.Globalization;
using FamilyMemories.Data;

namespace FamilyMemories.Exports;

public enum Medium { Website, ScreenPdf, PrintBook }

/// <summary>Everything the Book template needs (shareable PDF and print interior share it).</summary>
public sealed record BookView(
    ExportContent Content,
    BookOptions Options,
    Medium Medium,
    Func<Attachment, string> PhotoSrc,
    Func<ExportClip, string?> QrSrc,
    string TreeSvg,
    int BlankPages,
    string Css)
{
    public bool Print => Medium == Medium.PrintBook;

    /// <summary>@page rules: Letter with even margins for the screen PDF; trim size, mirrored margins with a gutter, and optional bleed for print.</summary>
    public string PageCss()
    {
        var ic = CultureInfo.InvariantCulture;
        if (!Print) return "@page { size: 8.5in 11in; margin: 0.85in 0.9in 0.9in 0.9in; }";
        var (w, h) = SpineCalculator.TrimInches(Options.Trim);
        var gutter = Options.Trim == TrimSize.Size6x9 ? 0.875 : 1.0;   // inside margin, a little more for thicker books
        var outer = Options.Trim == TrimSize.Size6x9 ? 0.625 : 0.75;
        var bleed = Options.Bleed ? $"bleed: {SpineCalculator.BleedInches.ToString(ic)}in;" : "";
        return string.Create(ic, $$"""
            @page { size: {{w}}in {{h}}in; margin-top: 0.8in; margin-bottom: 0.85in; {{bleed}} }
            @page :left { margin-left: {{outer}}in; margin-right: {{gutter}}in; }
            @page :right { margin-left: {{gutter}}in; margin-right: {{outer}}in; }
            """);
    }

    public string Year => Content.CreatedAt.Year.ToString(CultureInfo.InvariantCulture);
}

public sealed record CoverView(BookOptions Options, double WidthIn, double HeightIn, double SpineIn, double TrimWidthIn, string? FrontPhoto, string? BackPhoto, bool SpineText, string StyleSheet)
{
    public string Css()
    {
        var ic = CultureInfo.InvariantCulture;
        return string.Create(ic, $$"""
            @page { size: {{WidthIn}}in {{HeightIn}}in; margin: 0; }
            :root { --bleed: {{SpineCalculator.BleedInches}}in; --trim-w: {{TrimWidthIn}}in; --spine: {{SpineIn}}in; }
            """);
    }
}

/// <summary>A page of the static website.</summary>
public sealed record SitePage(ExportContent Content, string Title, string Active)
{
    public ExportStory? Story { get; init; }
    public ExportPerson? Person { get; init; }
    public string? TreeSvg { get; init; }
    /// <summary>The family archive, folder by folder (D-080).</summary>
    public IReadOnlyList<(string Folder, IReadOnlyList<ArchiveItem> Items)> Archive { get; init; } = [];
    public static string StoryHref(ExportStory s) => $"story-{s.Slug}.html";
    public static string PersonHref(ExportPerson p) => $"person-{p.Slug}.html";
    public static string Media(Attachment a, string file) => $"media/{a.Id:N}/{file}";
    /// <summary>Videos with a playable copy are exported as video.mp4; older ones not yet converted keep their own format.</summary>
    public IReadOnlySet<Guid> PlayableVideos { get; init; } = new HashSet<Guid>();
    public string VideoName(Attachment a) => PlayableVideos.Contains(a.Id) ? "video.mp4" : "video" + a.Extension;
}

/// <summary>Reads an export stylesheet and embeds its fonts as data URIs, so the document is self-contained.</summary>
public static class InlineCss
{
    public static string Load(string assetsDir, string cssFile)
    {
        var css = File.ReadAllText(Path.Combine(assetsDir, cssFile));
        return System.Text.RegularExpressions.Regex.Replace(css, @"url\(""([\w.-]+\.woff2)""\)", m =>
        {
            var font = Path.Combine(assetsDir, "fonts", m.Groups[1].Value);
            return File.Exists(font) ? $"url(\"data:font/woff2;base64,{Convert.ToBase64String(File.ReadAllBytes(font))}\")" : m.Value;
        });
    }
}

/// <summary>A printable packet of questions with writing lines (Phase 6, FEATURE_PAPER_PACKETS).</summary>
public sealed record PacketView(string Name, string Helper, IReadOnlyList<Prompt> Questions, string Css)
{
    public string FirstName => Name.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? Name;
    /// <summary>Fewer lines when there are more hints, so each question fits on one page.</summary>
    public int Lines(Prompt q) => 15 - Math.Min(4, q.Hints.Count);
}
