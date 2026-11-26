using System.Globalization;
using System.Text.Json;
using FamilyMemories.Configuration;
using FamilyMemories.Data;
using Hangfire;
using ImageMagick;
using Microsoft.EntityFrameworkCore;

namespace FamilyMemories.Media;

/// <summary>Queues derivative generation for an attachment (Hangfire in production; inline in tests).</summary>
public interface IMediaJobs
{
    void EnqueueProcessing(Guid attachmentId);
    void EnqueueTranscription(Guid attachmentId);
}

public sealed class HangfireMediaJobs(IBackgroundJobClient jobs) : IMediaJobs
{
    public void EnqueueProcessing(Guid attachmentId) =>
        jobs.Enqueue<MediaProcessor>(p => p.ProcessAsync(attachmentId, CancellationToken.None));

    public void EnqueueTranscription(Guid attachmentId) =>
        jobs.Enqueue<FamilyMemories.Extras.TranscriptionJob>(j => j.RunAsync(attachmentId, CancellationToken.None));
}

/// <summary>
/// Makes web-friendly copies of an upload; the original is never touched.
/// Photos (incl. iPhone HEIC): EXIF-rotated JPEGs at 2048px and 400px, all metadata (and so GPS) stripped.
/// Audio (Safari mp4/aac, Chrome webm/opus, anything else): AAC .m4a.
/// Video: a poster frame, plus an H.264/AAC .mp4 (at most 1920px) that plays on every iPad; already-H.264 videos are just repackaged.
/// </summary>
public sealed class MediaProcessor(AppDbContext db, MediaStorage storage, AppOptions options, IServiceProvider services, ILogger<MediaProcessor> log)
{
    public const int WebSize = 2048;
    public const int ThumbSize = 400;
    private static readonly TimeSpan ToolTimeout = TimeSpan.FromMinutes(30);

    [AutomaticRetry(Attempts = 2)]
    public async Task ProcessAsync(Guid attachmentId, CancellationToken ct)
    {
        var a = await db.Attachments.SingleOrDefaultAsync(x => x.Id == attachmentId, ct);
        if (a is null || a.State == ProcessingState.Ready) return;

        var original = storage.PathFor(a, MediaVariant.Original);
        try
        {
            switch (a.Kind)
            {
                case AttachmentKind.Photo:
                    ProcessPhoto(a, original, storage.PathFor(a, MediaVariant.Web), storage.PathFor(a, MediaVariant.Thumb));
                    await ApplyPhotoInfoAsync(a, PhotoMetadata.Read(original), ct);
                    break;
                case AttachmentKind.Audio:
                    await ProcessAudioAsync(a, original, ct);
                    break;
                case AttachmentKind.Video:
                    await ProcessVideoAsync(a, original, ct);
                    break;
                case AttachmentKind.Document:
                    await PreviewPdfAsync(a, original, ct);
                    break;
            }
            a.State = ProcessingState.Ready;
            a.ProcessingError = null;
            // Optional: write down what was said (Phase 6). The storyteller decides whether to use it.
            if (options.TranscriptionEnabled && a.IsVoiceRecording && a.Kind == AttachmentKind.Audio && a.TranscriptState == TranscriptState.None)
                a.TranscriptState = TranscriptState.Pending;
        }
        catch (Exception ex) when (ex is MagickException or InvalidOperationException or TimeoutException or IOException
                                       or System.ComponentModel.Win32Exception or JsonException)
        {
            log.LogError(ex, "Could not process attachment {AttachmentId}", a.Id);
            a.State = ProcessingState.Failed;
            a.ProcessingError = ex.Message.Length > 2000 ? ex.Message[..2000] : ex.Message;
        }
        await db.SaveChangesAsync(ct);
        if (a.TranscriptState == TranscriptState.Pending)
            services.GetRequiredService<IMediaJobs>().EnqueueTranscription(a.Id);
    }

    /// <summary>
    /// The photo's own caption, keywords and date (D-080). A caption fills an empty one; for an archive item, an empty
    /// description and date are filled too, and a title that's only the file name becomes the caption.
    /// </summary>
    private async Task ApplyPhotoInfoAsync(Attachment a, PhotoInfo info, CancellationToken ct)
    {
        a.Keywords = [.. info.Keywords];
        a.TakenAt = info.TakenAt;
        if (string.IsNullOrWhiteSpace(a.Caption) && info.Caption is not null) a.Caption = info.Caption;
        if (a.ArchiveItemId is not { } itemId) return;
        var item = await db.ArchiveItems.SingleOrDefaultAsync(i => i.Id == itemId, ct);
        if (item is null) return;
        if (info.Caption is { } caption)
        {
            if (item.Description is null) item.Description = caption;
            if (item.Title == Path.GetFileNameWithoutExtension(a.OriginalFileName))
                item.Title = caption.Length <= 120 ? caption : caption[..(caption[..120].LastIndexOf(' ') is > 40 and var cut ? cut : 120)] + "…";
        }
        if (item.WhenYear is null && item.WhenDecade is null && info.TakenAt is { } taken)
            (item.WhenYear, item.WhenMonth, item.WhenDay) = (taken.Year, taken.Month, taken.Day);
    }

    internal static void ProcessPhoto(Attachment a, string source, string webPath, string thumbPath)
    {
        using var image = new MagickImage(source);
        image.AutoOrient();
        a.Width = (int)image.Width;
        a.Height = (int)image.Height;
        WriteJpeg(image, webPath, WebSize, 85);
        WriteJpeg(image, thumbPath, ThumbSize, 80);
    }

    private static void WriteJpeg(MagickImage source, string path, uint maxSize, uint quality)
    {
        using var copy = (MagickImage)source.Clone();
        if (copy.Width > maxSize || copy.Height > maxSize)
            copy.Resize(new MagickGeometry(maxSize, maxSize) { Greater = true });
        copy.Strip(); // removes EXIF (including GPS), XMP, IPTC
        copy.ColorSpace = ColorSpace.sRGB;
        copy.Format = MagickFormat.Jpeg;
        copy.Quality = quality;
        copy.Settings.Interlace = Interlace.Jpeg;
        copy.Write(path);
    }

    /// <summary>A picture of a PDF's first page (web.jpg and thumb.jpg). A nicety: if it can't be made, the PDF is still there to open.</summary>
    private async Task PreviewPdfAsync(Attachment a, string original, CancellationToken ct)
    {
        if (a.ContentType != "application/pdf") return;
        var prefix = Path.Combine(storage.FolderFor(a), "page");
        try
        {
            var result = await ExternalTool.RunAsync(options.PdfToPpmPath,
                ["-jpeg", "-r", "110", "-f", "1", "-l", "1", "-singlefile", original, prefix], TimeSpan.FromMinutes(2), ct);
            if (!result.Succeeded || !File.Exists(prefix + ".jpg"))
            {
                log.LogWarning("No preview for PDF {AttachmentId}: {Error}", a.Id, result.StdErr);
                return;
            }
            ProcessPhoto(a, prefix + ".jpg", storage.PathFor(a, MediaVariant.Web), storage.PathFor(a, MediaVariant.Thumb));
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or TimeoutException or IOException or MagickException)
        {
            log.LogWarning(ex, "No preview for PDF {AttachmentId}", a.Id);
            a.Width = a.Height = null;
        }
        finally
        {
            try { File.Delete(prefix + ".jpg"); } catch (IOException) { }
        }
    }

    private async Task ProcessAudioAsync(Attachment a, string original, CancellationToken ct)
    {
        var target = storage.PathFor(a, MediaVariant.Audio);
        List<string> args = ["-hide_banner", "-loglevel", "error", "-y", "-i", original, "-vn", "-map_metadata", "-1"];
        if (a.IsVoiceRecording) args.AddRange(["-ac", "1"]);
        args.AddRange(["-c:a", "aac", "-b:a", a.IsVoiceRecording ? "96k" : "160k", "-movflags", "+faststart", target]);

        var result = await ExternalTool.RunAsync(options.FfmpegPath, args, ToolTimeout, ct);
        if (!result.Succeeded) throw new InvalidOperationException("ffmpeg could not convert the audio: " + result.StdErr);
        a.DurationSeconds = (await ProbeAsync(target, ct)).Duration;
    }

    private async Task ProcessVideoAsync(Attachment a, string original, CancellationToken ct)
    {
        var probe = await ProbeAsync(original, ct);
        if (!probe.HasVideo)
        {
            // An .mp4/.webm with only sound (e.g. a phone voice memo) is really audio.
            a.Kind = AttachmentKind.Audio;
            await ProcessAudioAsync(a, original, ct);
            return;
        }

        a.DurationSeconds = probe.Duration;
        var at = probe.Duration is { } d && d > 2 ? 1.0 : 0.0;
        var poster = storage.PathFor(a, MediaVariant.Poster);
        var result = await ExternalTool.RunAsync(options.FfmpegPath,
            ["-hide_banner", "-loglevel", "error", "-y", "-ss", at.ToString(CultureInfo.InvariantCulture), "-i", original,
             "-frames:v", "1", "-q:v", "3", poster], ToolTimeout, ct);
        if (!result.Succeeded) throw new InvalidOperationException("ffmpeg could not make a poster frame: " + result.StdErr);

        ProcessPhoto(a, poster, poster, storage.PathFor(a, MediaVariant.Thumb));
        await MakePlayableVideoAsync(a, original, probe, ct);
    }

    /// <summary>Writes video.mp4: H.264 (yuv420p, ≤1920px) with AAC sound and the index at the front, so Safari can stream it.</summary>
    internal async Task MakePlayableVideoAsync(Attachment a, string original, Probe probe, CancellationToken ct)
    {
        var target = storage.PathFor(a, MediaVariant.Video);
        var temp = target + ".tmp.mp4";
        List<string> args = ["-hide_banner", "-loglevel", "error", "-y", "-i", original, "-map", "0:v:0", "-map", "0:a:0?", "-map_metadata", "-1"];
        if (probe.IsSafariReady)
            args.AddRange(["-c", "copy"]);
        else
            args.AddRange(["-c:v", "libx264", "-preset", "veryfast", "-crf", "23", "-pix_fmt", "yuv420p",
                           "-vf", "scale='if(gt(iw,ih),min(1920,iw),-2)':'if(gt(iw,ih),-2,min(1920,ih))'",
                           "-c:a", "aac", "-b:a", "128k"]);
        args.AddRange(["-movflags", "+faststart", temp]);
        var result = await ExternalTool.RunAsync(options.FfmpegPath, args, TimeSpan.FromHours(3), ct);
        if (!result.Succeeded)
        {
            try { File.Delete(temp); } catch (IOException) { }
            throw new InvalidOperationException("ffmpeg could not make a playable copy of the video: " + result.StdErr);
        }
        File.Move(temp, target, overwrite: true);
    }

    /// <summary>
    /// Videos uploaded before playable copies existed (v0.8 and earlier) get one, a few at a time.
    /// If it fails, the original keeps being served.
    /// </summary>
    [AutomaticRetry(Attempts = 0)]
    public async Task BackfillPlayableVideosAsync(CancellationToken ct)
    {
        var videos = await db.Attachments.Where(a => a.Kind == AttachmentKind.Video && a.State == ProcessingState.Ready).ToListAsync(ct);
        var done = 0;
        foreach (var a in videos.Where(a => !File.Exists(storage.PathFor(a, MediaVariant.Video))).Take(5))
        {
            var original = storage.PathFor(a, MediaVariant.Original);
            if (!File.Exists(original)) continue;
            try { await MakePlayableVideoAsync(a, original, await ProbeAsync(original, ct), ct); done++; }
            catch (Exception ex) when (ex is InvalidOperationException or TimeoutException or IOException or JsonException)
            {
                log.LogWarning(ex, "Could not make a playable copy of video {AttachmentId}", a.Id);
            }
        }
        if (done > 0) log.LogInformation("Made playable copies of {Count} older videos", done);
    }

    internal sealed record Probe(double? Duration, bool HasVideo, bool HasAudio,
        string? VideoCodec = null, string? PixelFormat = null, int Width = 0, int Height = 0, string? AudioCodec = null, string? Container = null)
    {
        /// <summary>H.264 4:2:0 at most 1920px, with AAC (or no) sound, in an MP4/MOV: copy it as it is.</summary>
        public bool IsSafariReady =>
            VideoCodec == "h264" && PixelFormat is "yuv420p" or "yuvj420p" && Math.Max(Width, Height) <= 1920
            && (AudioCodec is null or "aac") && Container is not null && Container.Contains("mp4");
    }

    internal async Task<Probe> ProbeAsync(string path, CancellationToken ct)
    {
        var result = await ExternalTool.RunAsync(options.FfprobePath,
            ["-v", "error", "-print_format", "json", "-show_format", "-show_streams", path], TimeSpan.FromMinutes(2), ct);
        if (!result.Succeeded) throw new InvalidOperationException("ffprobe could not read the file: " + result.StdErr);

        using var json = JsonDocument.Parse(result.StdOut);
        double? duration = null;
        if (json.RootElement.TryGetProperty("format", out var format) && format.TryGetProperty("duration", out var dur)
            && double.TryParse(dur.GetString(), CultureInfo.InvariantCulture, out var seconds))
            duration = seconds;

        bool video = false, audio = false;
        string? videoCodec = null, pixFmt = null, audioCodec = null;
        int width = 0, height = 0;
        var container = format.ValueKind == JsonValueKind.Object && format.TryGetProperty("format_name", out var fn) ? fn.GetString() : null;
        if (json.RootElement.TryGetProperty("streams", out var streams))
        {
            foreach (var s in streams.EnumerateArray())
            {
                var type = s.TryGetProperty("codec_type", out var t) ? t.GetString() : null;
                // Cover art in audio files shows up as a one-frame "video" stream; ignore it.
                var isCoverArt = s.TryGetProperty("disposition", out var disp) && disp.TryGetProperty("attached_pic", out var pic) && pic.GetInt32() == 1;
                if (type == "video" && !isCoverArt && !video)
                {
                    video = true;
                    videoCodec = s.TryGetProperty("codec_name", out var vc) ? vc.GetString() : null;
                    pixFmt = s.TryGetProperty("pix_fmt", out var pf) ? pf.GetString() : null;
                    width = s.TryGetProperty("width", out var w) ? w.GetInt32() : 0;
                    height = s.TryGetProperty("height", out var h) ? h.GetInt32() : 0;
                }
                if (type == "audio" && !audio)
                {
                    audio = true;
                    audioCodec = s.TryGetProperty("codec_name", out var ac) ? ac.GetString() : null;
                }
            }
        }
        return new Probe(duration, video, audio, videoCodec, pixFmt, width, height, audioCodec, container);
    }
}
