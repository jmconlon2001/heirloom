using System.Text;
using FamilyMemories.Data;
using FamilyMemories.Media;
using FamilyMemories.Tests.Infrastructure;
using FamilyMemories.Web;

namespace FamilyMemories.Tests;

public sealed class FuzzyDateTests
{
    private static FuzzyDate N(int? y = null, int? m = null, int? d = null, bool circa = false, int? decade = null, string? label = null) =>
        FuzzyDate.Normalize(y, m, d, circa, decade, label, currentYear: 2026);

    [Theory]
    [InlineData(1965, null, null, true, null, null, "around 1965")]
    [InlineData(1965, null, null, false, null, null, "1965")]
    [InlineData(null, null, null, false, 1970, null, "1970s")]
    [InlineData(null, null, null, true, 1940, null, "around 1940s")]
    [InlineData(1972, 6, null, false, null, null, "June 1972")]
    [InlineData(1972, 6, 3, false, null, null, "June 3, 1972")]
    [InlineData(1945, null, null, true, null, "summer after the war", "summer after the war (around 1945)")]
    [InlineData(null, null, null, false, null, "summer after the war", "summer after the war")]
    [InlineData(null, null, null, false, null, null, "")]
    public void Formats_like_people_talk(int? y, int? m, int? d, bool circa, int? decade, string? label, string expected) =>
        Assert.Equal(expected, N(y, m, d, circa, decade, label).Format());

    [Fact]
    public void Impossible_parts_are_dropped_not_rejected()
    {
        Assert.Equal(new FuzzyDate(1972, 2, null, false, null, null), N(1972, 2, 30));          // no Feb 30
        Assert.Equal(new FuzzyDate(null, null, null, false, null, null), N(y: 3000));            // future
        Assert.Equal(new FuzzyDate(1500, null, null, false, null, null), N(y: 1500));            // family trees go back centuries (D-082)
        Assert.Equal(new FuzzyDate(null, null, null, false, null, null), N(y: 999));             // too early: a slip
        Assert.Equal(new FuzzyDate(1960, null, null, false, null, null), N(1960, 13, 5));        // month 13, so day goes too
        Assert.Equal(new FuzzyDate(null, null, null, false, null, null), N(m: 6));               // month with no year
        Assert.Equal(new FuzzyDate(null, null, null, false, null, null), N(decade: 1975));       // not a decade
        Assert.False(N(circa: true).Circa);                                                       // "around" nothing
    }

    [Fact]
    public void A_year_wins_over_a_decade() => Assert.Equal("1966", N(1966, decade: 1960).Format());

    [Fact]
    public void Leap_days_are_kept() => Assert.Equal("February 29, 1964", N(1964, 2, 29).Format());

    [Fact]
    public void Labels_are_trimmed_and_capped()
    {
        Assert.Equal("the war", N(label: "  the war  ").Label);
        Assert.Equal(200, N(label: new string('x', 500)).Label!.Length);
        Assert.Null(N(label: "   ").Label);
    }

    [Fact]
    public void Timeline_sort_uses_the_middle_of_what_is_known()
    {
        Assert.Equal(new DateOnly(1975, 1, 1), N(decade: 1970).SortDate);
        Assert.Equal(new DateOnly(1972, 6, 15), N(1972, 6).SortDate);
        Assert.Equal(new DateOnly(1972, 7, 1), N(1972).SortDate);
        Assert.Null(N(label: "once").SortDate);
    }
}

public sealed class FileSignatureTests
{
    private static DetectedFile? Of(params byte[] bytes) => FileSignatures.Detect(bytes);
    private static DetectedFile? OfAscii(string s, int pad = 16) => FileSignatures.Detect(Encoding.ASCII.GetBytes(s.PadRight(pad, '\0')));

    [Theory]
    [InlineData("photo.heic", AttachmentKind.Photo, "image/heic")]
    [InlineData("photo.jpg", AttachmentKind.Photo, "image/jpeg")]
    [InlineData("voice-safari.mp4", AttachmentKind.Video, "video/mp4")] // audio-only mp4: ffprobe reclassifies later
    [InlineData("voice-chrome.webm", AttachmentKind.Video, "video/webm")]
    public async Task Real_files_are_recognised(string fixture, AttachmentKind kind, string type)
    {
        var detected = await FileSignatures.DetectAsync(AppFactory.Fixture(fixture));
        Assert.NotNull(detected);
        Assert.Equal(kind, detected.Kind);
        Assert.Equal(type, detected.ContentType);
    }

    [Fact]
    public void Common_formats_by_their_first_bytes()
    {
        Assert.Equal("image/png", Of(0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A)!.ContentType);
        Assert.Equal("application/pdf", OfAscii("%PDF-1.7")!.ContentType);
        Assert.Equal("image/gif", OfAscii("GIF89a")!.ContentType);
        Assert.Equal("image/webp", OfAscii("RIFF\0\0\0\0WEBPVP8 ")!.ContentType);
        Assert.Equal("audio/wav", OfAscii("RIFF\0\0\0\0WAVEfmt ")!.ContentType);
        Assert.Equal("audio/mpeg", OfAscii("ID3\u0004")!.ContentType);
        Assert.Equal("audio/mpeg", Of(0xFF, 0xFB, 0x90, 0x00)!.ContentType);
        Assert.Equal("audio/aac", Of(0xFF, 0xF1, 0x50, 0x80)!.ContentType);
        Assert.Equal("audio/ogg", OfAscii("OggS")!.ContentType);
        Assert.Equal("audio/mp4", OfAscii("\0\0\0\u0018ftypM4A ")!.ContentType);
        Assert.Equal("video/quicktime", OfAscii("\0\0\0\u0014ftypqt  ")!.ContentType);
        Assert.Equal("image/tiff", Of(0x49, 0x49, 0x2A, 0x00)!.ContentType);
    }

    [Theory]
    [InlineData("MZ")]                 // Windows program
    [InlineData("<html>")]             // web page
    [InlineData("PK\u0003\u0004")]     // zip / docx
    [InlineData("#!/bin/sh")]
    [InlineData("")]
    public void Everything_else_is_refused(string start) => Assert.Null(OfAscii(start));

    [Fact]
    public void A_renamed_file_is_judged_by_its_contents()
    {
        // An HTML file named "photo.jpg" is still HTML.
        Assert.Null(FileSignatures.Detect(Encoding.ASCII.GetBytes("<!doctype html><script>alert(1)</script>")));
    }
}

public sealed class DeviceNameTests
{
    [Theory]
    [InlineData("Mozilla/5.0 (iPad; CPU OS 18_0 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/18.0 Mobile/15E148 Safari/604.1", "iPad (Safari)")]
    [InlineData("Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/18.0 Mobile/15E148 Safari/604.1", "iPad (Safari)")]
    [InlineData("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/130.0 Safari/537.36 Edg/130.0", "Windows computer (Edge)")]
    [InlineData("Mozilla/5.0 (Linux; Android 14; Pixel 8) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/130.0 Mobile Safari/537.36", "Android phone (Chrome)")]
    [InlineData(null, "Unknown device")]
    public void Recognisable_names(string? ua, string expected) => Assert.Equal(expected, DeviceNames.Describe(ua));
}
