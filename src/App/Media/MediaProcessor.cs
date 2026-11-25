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
}

public sealed class HangfireMediaJobs(IBackgroundJobClient jobs) : IMediaJobs
{
    public void EnqueueProcessing(Guid attachmentId) =>
        jobs.Enqueue<MediaProcessor>(p => p.ProcessAsync(attachmentId, CancellationToken.None));
}

/// <summary>
/// Makes web-friendly copies of an upload; the original is never touched.
/// Photos (incl. iPhone HEIC): EXIF-rotated JPEGs at 2048px and 400px, all metadata (and so GPS) stripped.
/// Audio (Safari mp4/aac, Chrome webm/opus, anything else): AAC .m4a. Video: a poster frame.
/// </summary>
public sealed class MediaProcessor(AppDbContext db, MediaStorage storage, AppOptions options, ILogger<MediaProcessor> log)
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
                    break;
                case AttachmentKind.Audio:
                    await ProcessAudioAsync(a, original, ct);
                    break;
                case AttachmentKind.Video:
                    await ProcessVideoAsync(a, original, ct);
                    break;
                case AttachmentKind.Document:
                    break; // PDFs are shown as a download card; no preview in v1.
            }
            a.State = ProcessingState.Ready;
            a.ProcessingError = null;
        }
        catch (Exception ex) when (ex is MagickException or InvalidOperationException or TimeoutException or IOException
                                       or System.ComponentModel.Win32Exception or JsonException)
        {
            log.LogError(ex, "Could not process attachment {AttachmentId}", a.Id);
            a.State = ProcessingState.Failed;
            a.ProcessingError = ex.Message.Length > 2000 ? ex.Message[..2000] : ex.Message;
        }
        await db.SaveChangesAsync(ct);
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
    }

    internal sealed record Probe(double? Duration, bool HasVideo, bool HasAudio);

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
        if (json.RootElement.TryGetProperty("streams", out var streams))
        {
            foreach (var s in streams.EnumerateArray())
            {
                var type = s.TryGetProperty("codec_type", out var t) ? t.GetString() : null;
                // Cover art in audio files shows up as a one-frame "video" stream; ignore it.
                var isCoverArt = s.TryGetProperty("disposition", out var disp) && disp.TryGetProperty("attached_pic", out var pic) && pic.GetInt32() == 1;
                if (type == "video" && !isCoverArt) video = true;
                if (type == "audio") audio = true;
            }
        }
        return new Probe(duration, video, audio);
    }
}
