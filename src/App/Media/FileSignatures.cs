using System.Text;
using FamilyMemories.Data;

namespace FamilyMemories.Media;

public sealed record DetectedFile(AttachmentKind Kind, string ContentType, string Extension);

/// <summary>
/// Identifies uploads by their first bytes ("magic numbers"), never by file name or the
/// browser's claimed type. Anything not on this list is refused.
/// </summary>
public static class FileSignatures
{
    public const int HeaderLength = 64;

    private static readonly HashSet<string> HeifBrands = ["heic", "heix", "hevc", "hevx", "heim", "heis", "hevm", "hevs", "mif1", "msf1"];
    private static readonly HashSet<string> AvifBrands = ["avif", "avis"];

    public static DetectedFile? Detect(ReadOnlySpan<byte> h)
    {
        if (Starts(h, [0xFF, 0xD8, 0xFF])) return new(AttachmentKind.Photo, "image/jpeg", ".jpg");
        if (Starts(h, [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A])) return new(AttachmentKind.Photo, "image/png", ".png");
        if (Ascii(h, 0, "GIF87a") || Ascii(h, 0, "GIF89a")) return new(AttachmentKind.Photo, "image/gif", ".gif");
        if (Ascii(h, 0, "RIFF") && Ascii(h, 8, "WEBP")) return new(AttachmentKind.Photo, "image/webp", ".webp");
        if (Starts(h, [0x49, 0x49, 0x2A, 0x00]) || Starts(h, [0x4D, 0x4D, 0x00, 0x2A])) return new(AttachmentKind.Photo, "image/tiff", ".tif");
        if (Ascii(h, 0, "%PDF-")) return new(AttachmentKind.Document, "application/pdf", ".pdf");

        if (Ascii(h, 4, "ftyp") && h.Length >= 12)
        {
            var brand = Encoding.ASCII.GetString(h.Slice(8, 4));
            if (HeifBrands.Contains(brand)) return new(AttachmentKind.Photo, "image/heic", ".heic");
            if (AvifBrands.Contains(brand)) return new(AttachmentKind.Photo, "image/avif", ".avif");
            if (brand is "M4A " or "M4B ") return new(AttachmentKind.Audio, "audio/mp4", ".m4a");
            if (brand == "qt  ") return new(AttachmentKind.Video, "video/quicktime", ".mov");
            // isom, mp41, mp42, iso2..6, avc1, M4V, 3gp…: video or audio-only; ffprobe decides during processing.
            return new(AttachmentKind.Video, "video/mp4", ".mp4");
        }

        if (Starts(h, [0x1A, 0x45, 0xDF, 0xA3])) return new(AttachmentKind.Video, "video/webm", ".webm");
        if (Ascii(h, 0, "ID3") || (h.Length >= 2 && h[0] == 0xFF && (h[1] & 0xE6) == 0xE2)) return new(AttachmentKind.Audio, "audio/mpeg", ".mp3");
        if (h.Length >= 2 && h[0] == 0xFF && (h[1] & 0xF6) == 0xF0) return new(AttachmentKind.Audio, "audio/aac", ".aac");
        if (Ascii(h, 0, "RIFF") && Ascii(h, 8, "WAVE")) return new(AttachmentKind.Audio, "audio/wav", ".wav");
        if (Ascii(h, 0, "OggS")) return new(AttachmentKind.Audio, "audio/ogg", ".ogg");
        if (Ascii(h, 0, "fLaC")) return new(AttachmentKind.Audio, "audio/flac", ".flac");
        return null;
    }

    public static async Task<DetectedFile?> DetectAsync(string path)
    {
        var buffer = new byte[HeaderLength];
        await using var stream = File.OpenRead(path);
        var read = await stream.ReadAtLeastAsync(buffer, HeaderLength, throwOnEndOfStream: false);
        return Detect(buffer.AsSpan(0, read));
    }

    private static bool Starts(ReadOnlySpan<byte> h, ReadOnlySpan<byte> sig) => h.StartsWith(sig);

    private static bool Ascii(ReadOnlySpan<byte> h, int offset, string text) =>
        h.Length >= offset + text.Length && h.Slice(offset, text.Length).SequenceEqual(Encoding.ASCII.GetBytes(text));
}
