using System.Globalization;
using System.Text;
using FamilyMemories.Data;

namespace FamilyMemories.Exports;

/// <summary>preflight.txt (§9c): what a printer would complain about, in plain words, before you order.</summary>
public static class Preflight
{
    public const double MaxPhotoHeightInches = 5;

    /// <summary>How wide a photo is printed: the text block's width, or less if the photo is tall.</summary>
    public static double PrintedWidthInches(int pixelWidth, int pixelHeight, BookOptions o)
    {
        var (trimW, _) = SpineCalculator.TrimInches(o.Trim);
        var textWidth = trimW - (o.Trim == TrimSize.Size6x9 ? 0.875 + 0.625 : 1.0 + 0.75);
        if (pixelWidth <= 0 || pixelHeight <= 0) return textWidth;
        return Math.Min(textWidth, MaxPhotoHeightInches * pixelWidth / pixelHeight);
    }

    public static double EffectiveDpi(int pixelWidth, int pixelHeight, BookOptions o) =>
        pixelWidth <= 0 ? 0 : pixelWidth / PrintedWidthInches(pixelWidth, pixelHeight, o);

    public static string Report(ExportContent content, BookOptions o, int pages, int padding,
        IReadOnlyDictionary<Guid, (int W, int H)> photoSizes, IReadOnlyList<Chapter> chapters, ExportScope scope, string pandocNote)
    {
        var ic = CultureInfo.InvariantCulture;
        var (trimW, trimH) = SpineCalculator.TrimInches(o.Trim);
        var spine = SpineCalculator.SpineWidthInches(pages, o.PagesPerInch);
        var (coverW, coverH) = SpineCalculator.CoverInches(o.Trim, pages, o.PagesPerInch);
        var paper = SpineCalculator.Presets.TryGetValue(o.PaperPreset, out var preset) ? preset.Label : "custom paper";
        var sb = new StringBuilder();
        sb.AppendLine(ic, $"PREFLIGHT REPORT: {o.Title}");
        sb.AppendLine(new string('=', 40));
        sb.AppendLine(ic, $"Trim size:     {trimW} x {trimH} in{(o.Bleed ? $" (with {SpineCalculator.BleedInches} in bleed)" : ", no bleed")}");
        sb.AppendLine(ic, $"Pages:         {pages}{(padding > 0 ? $" (includes {padding} blank page{(padding == 1 ? "" : "s")} so the count is a multiple of {o.PageMultiple})" : "")}");
        sb.AppendLine(ic, $"Paper:         {paper}, {o.PagesPerInch:0.#} pages per inch");
        sb.AppendLine(ic, $"Spine width:   {spine:0.###} in ({spine * 25.4:0.#} mm){(SpineCalculator.SpineCanHoldText(spine) ? "" : ": too narrow for text, so the spine is left plain")}");
        sb.AppendLine(ic, $"Cover spread:  {coverW:0.###} x {coverH:0.###} in (back + spine + front, with bleed)");
        sb.AppendLine(ic, $"Stories:       {content.Stories.Count}");
        sb.AppendLine();

        var photos = content.Stories.SelectMany(s => s.Photos.Select(p => (Story: s, Photo: p))).ToList();
        var lowRes = photos
            .Select(x => (x.Story, x.Photo, Size: photoSizes.GetValueOrDefault(x.Photo.Attachment.Id)))
            .Select(x => (x.Story, x.Photo, x.Size, Dpi: EffectiveDpi(x.Size.W, x.Size.H, o)))
            .Where(x => x.Dpi < 300).ToList();
        sb.AppendLine(ic, $"PHOTOS BELOW 300 DPI AT PRINTED SIZE ({lowRes.Count})");
        if (lowRes.Count == 0) sb.AppendLine("  None. Every photo is sharp enough to print.");
        foreach (var x in lowRes)
            sb.AppendLine(ic, $"  - {Name(x.Photo)} in \"{x.Story.Memory.Title}\": {x.Size.W} px wide printed at {PrintedWidthInches(x.Size.W, x.Size.H, o):0.##} in = {x.Dpi:0} dpi{(x.Dpi < 150 ? " (will look blurry)" : " (probably fine for family photos)")}");
        sb.AppendLine();

        var noCaption = photos.Where(x => string.IsNullOrWhiteSpace(x.Photo.Caption)).ToList();
        sb.AppendLine(ic, $"PHOTOS WITHOUT A CAPTION ({noCaption.Count})");
        if (noCaption.Count == 0) sb.AppendLine("  None.");
        foreach (var x in noCaption) sb.AppendLine(ic, $"  - {x.Photo.Attachment.OriginalFileName} in \"{x.Story.Memory.Title}\"");
        sb.AppendLine();

        var inScope = scope.ChapterIds.Count > 0 ? chapters.Where(c => scope.ChapterIds.Contains(c.Id)) : o.Organize == OrganizeBy.Chapter ? chapters : [];
        var empty = inScope.Where(c => content.Stories.All(s => s.ChapterTitle != c.Title)).ToList();
        sb.AppendLine(ic, $"EMPTY CHAPTERS ({empty.Count})");
        if (empty.Count == 0) sb.AppendLine("  None.");
        foreach (var c in empty) sb.AppendLine(ic, $"  - {c.Title}");
        sb.AppendLine();

        var noWords = content.Stories.Where(s => !s.HasWords).ToList();
        if (noWords.Count > 0)
        {
            sb.AppendLine(ic, $"STORIES WITH NO WRITTEN WORDS ({noWords.Count}); only photos or recordings will be printed");
            foreach (var s in noWords) sb.AppendLine(ic, $"  - \"{s.Memory.Title}\" by {s.Teller}");
            sb.AppendLine();
        }

        sb.AppendLine(pandocNote);
        sb.AppendLine();
        sb.AppendLine("Before ordering: upload interior.pdf and cover.pdf to your printer and check their preview,");
        sb.AppendLine("especially that the spine width matches their cover calculator for your page count and paper.");
        return sb.ToString();
    }

    private static string Name(ExportPhoto p) => string.IsNullOrWhiteSpace(p.Caption) ? p.Attachment.OriginalFileName : $"\"{p.Caption}\"";
}
