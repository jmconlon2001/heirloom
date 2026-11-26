using System.Globalization;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using ImageMagick;

namespace FamilyMemories.Media;

/// <summary>What a photo says about itself: caption, keywords and when it was taken (D-080).</summary>
public sealed record PhotoInfo(string? Caption, IReadOnlyList<string> Keywords, DateTime? TakenAt);

/// <summary>
/// Reads a photo's own metadata before the web copies are stripped: EXIF (date taken, description), IPTC (caption,
/// keywords) and XMP (dc:description, dc:subject, Lightroom's lr:hierarchicalSubject). The original keeps all of it.
/// </summary>
public static partial class PhotoMetadata
{
    private static readonly XNamespace Rdf = "http://www.w3.org/1999/02/22-rdf-syntax-ns#";
    private static readonly XNamespace Dc = "http://purl.org/dc/elements/1.1/";
    private static readonly XNamespace Lr = "http://ns.adobe.com/lightroom/1.0/";

    public static PhotoInfo Read(string path)
    {
        try
        {
            using var image = new MagickImage();
            image.Ping(path);   // metadata only, no pixels
            return Read(image);
        }
        catch (MagickException)
        {
            return new PhotoInfo(null, [], null);
        }
    }

    internal static PhotoInfo Read(IMagickImage image)
    {
        string? caption = null;
        var keywords = new List<string>();
        DateTime? taken = null;

        if (image.GetXmpProfile()?.ToXDocument() is { } xmp)
        {
            caption = Clean(xmp.Descendants(Dc + "description").Descendants(Rdf + "li").FirstOrDefault()?.Value);
            keywords.AddRange(xmp.Descendants(Lr + "hierarchicalSubject").Descendants(Rdf + "li").Select(li => li.Value));
            keywords.AddRange(xmp.Descendants(Dc + "subject").Descendants(Rdf + "li").Select(li => li.Value));
        }
        if (image.GetIptcProfile() is { } iptc)
        {
            caption ??= Clean(iptc.GetValue(IptcTag.Caption)?.Value);
            keywords.AddRange(iptc.GetAllValues(IptcTag.Keyword).Select(v => v.Value));
        }
        if (image.GetExifProfile() is { } exif)
        {
            caption ??= Clean(exif.GetValue(ExifTag.ImageDescription)?.Value);
            var when = exif.GetValue(ExifTag.DateTimeOriginal)?.Value ?? exif.GetValue(ExifTag.DateTimeDigitized)?.Value;
            if (DateTime.TryParseExact(when?.Trim('\0', ' '), "yyyy:MM:dd HH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.None, out var t) && t.Year > 1800)
                taken = DateTime.SpecifyKind(t, DateTimeKind.Utc);   // the camera's wall-clock time; cameras don't record a zone
        }

        // A flat keyword that's just the last part of a hierarchical one adds nothing.
        var cleaned = keywords.Select(k => k.Trim()).Where(k => k.Length is > 0 and <= 200).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var leaves = cleaned.Where(k => k.Contains('|')).Select(k => k[(k.LastIndexOf('|') + 1)..]).ToHashSet(StringComparer.OrdinalIgnoreCase);
        cleaned = [.. cleaned.Where(k => k.Contains('|') || !leaves.Contains(k)).Take(100)];
        return new PhotoInfo(caption, cleaned, taken);
    }

    /// <summary>Cameras write things like "OLYMPUS DIGITAL CAMERA" into the description; that's not a caption.</summary>
    private static string? Clean(string? text)
    {
        var t = text?.Trim('\0', ' ', '\n', '\r', '\t');
        if (string.IsNullOrEmpty(t) || CameraJunk().IsMatch(t)) return null;
        return t.Length > 1000 ? t[..1000] : t;
    }

    [GeneratedRegex(@"^(?:[A-Z0-9 _.-]*(?:DIGITAL CAMERA|DSC|CAMERA|IMAGE)[A-Z0-9 _.-]*|default|x-default|Description)$")]
    private static partial Regex CameraJunk();
}
