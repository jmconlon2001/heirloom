namespace FamilyMemories.Exports;

public sealed record PaperPreset(string Label, double PagesPerInch);

/// <summary>
/// Cover geometry for print-on-demand (§9c). Spine width = page count ÷ pages-per-inch of the paper.
/// Presets match the printers' published paper thicknesses; always check against the printer's own cover calculator.
/// </summary>
public static class SpineCalculator
{
    public static readonly IReadOnlyDictionary<string, PaperPreset> Presets = new Dictionary<string, PaperPreset>
    {
        // Amazon KDP: white paper 0.002252 in/page, cream 0.0025 in/page.
        ["kdp-white"] = new("Amazon KDP, white paper", 1 / 0.002252),
        ["kdp-cream"] = new("Amazon KDP, cream paper", 1 / 0.0025),
        // Lulu standard paper: about 444 pages per inch. Check Lulu's cover calculator before ordering.
        ["lulu-white"] = new("Lulu, standard paper (check Lulu's calculator)", 444),
    };

    public const double BleedInches = 0.125;

    public static double SpineWidthInches(int pages, double pagesPerInch) =>
        pages <= 0 || pagesPerInch <= 0 ? 0 : Math.Round(pages / pagesPerInch, 4);

    public static (double Width, double Height) TrimInches(TrimSize trim) => trim switch
    {
        TrimSize.Size6x9 => (6, 9),
        TrimSize.Size8x10 => (8, 10),
        _ => (8.5, 11),
    };

    /// <summary>Full cover spread: back + spine + front, plus bleed all round.</summary>
    public static (double Width, double Height) CoverInches(TrimSize trim, int pages, double pagesPerInch)
    {
        var (w, h) = TrimInches(trim);
        return (Math.Round(2 * w + SpineWidthInches(pages, pagesPerInch) + 2 * BleedInches, 4), h + 2 * BleedInches);
    }

    /// <summary>Pages to add so the count is a multiple of <paramref name="multiple"/>.</summary>
    public static int PaddingPages(int pages, int multiple) =>
        multiple <= 1 || pages % multiple == 0 ? 0 : multiple - pages % multiple;

    /// <summary>Spines narrower than about a quarter inch can't carry readable text.</summary>
    public static bool SpineCanHoldText(double spineInches) => spineInches >= 0.25;
}
