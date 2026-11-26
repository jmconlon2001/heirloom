using FamilyMemories.Configuration;
using FamilyMemories.Data;

namespace FamilyMemories.Media;

public enum MediaVariant { Original, Web, Thumb, Audio, Poster, Video }

/// <summary>
/// File layout under /data/media: {memoryId}/{attachmentId}/original.ext plus derivatives
/// (web.jpg 2048px, thumb.jpg 400px, audio.m4a, poster.jpg, video.mp4 that every iPad plays). Originals are never modified.
/// </summary>
public sealed class MediaStorage(AppOptions options)
{
    public string FolderFor(Attachment a) => Path.Combine(options.MediaDir, a.MemoryId.ToString("N"), a.Id.ToString("N"));

    public string PathFor(Attachment a, MediaVariant variant) => Path.Combine(FolderFor(a), variant switch
    {
        MediaVariant.Original => "original" + a.Extension,
        MediaVariant.Web => "web.jpg",
        MediaVariant.Thumb => "thumb.jpg",
        MediaVariant.Audio => "audio.m4a",
        MediaVariant.Poster => "poster.jpg",
        MediaVariant.Video => "video.mp4",
        _ => throw new ArgumentOutOfRangeException(nameof(variant)),
    });

    public string UploadPartPath(Guid uploadId) => Path.Combine(options.UploadsDir, uploadId.ToString("N") + ".part");

    public static string ContentTypeFor(Attachment a, MediaVariant variant) => variant switch
    {
        MediaVariant.Original => a.ContentType,
        MediaVariant.Audio => "audio/mp4",
        MediaVariant.Video => "video/mp4",
        _ => "image/jpeg",
    };
}
