using System.Security.Cryptography;
using FamilyMemories.Configuration;
using FamilyMemories.Data;
using Microsoft.EntityFrameworkCore;

namespace FamilyMemories.Media;

public enum UploadError
{
    None,
    NotFound,
    /// <summary>The chunk didn't start where the server is; the client should resume from <see cref="UploadStatus.Received"/>.</summary>
    WrongOffset,
    TooLarge,
    Unsupported,
    Incomplete,
}

public sealed record UploadStatus(Guid UploadId, long Received, long Total, UploadError Error = UploadError.None, Attachment? Attachment = null);

/// <summary>
/// Resumable chunked uploads (the browser sends 4 MB pieces and can pick up where it left off
/// after a dropped connection), plus the plain form upload used when JavaScript is off.
/// </summary>
public sealed class AttachmentService(
    AppDbContext db,
    MediaStorage storage,
    IMediaJobs jobs,
    AppOptions options,
    TimeProvider clock)
{
    public const int MaxChunkBytes = 8 * 1024 * 1024;
    public const int ChunkBytes = 4 * 1024 * 1024;

    private DateTime Now => clock.GetUtcNow().UtcDateTime;
    private long LargestAllowed => Math.Max(options.MaxVideoBytes, Math.Max(options.MaxAudioBytes, options.MaxImageBytes));

    public long LimitFor(AttachmentKind kind) => kind switch
    {
        AttachmentKind.Video => options.MaxVideoBytes,
        AttachmentKind.Audio => options.MaxAudioBytes,
        _ => options.MaxImageBytes,
    };

    public async Task<UploadStatus> StartAsync(Guid userId, Guid memoryId, string fileName, long totalBytes, bool isVoice)
    {
        if (totalBytes <= 0) return new(Guid.Empty, 0, totalBytes, UploadError.Incomplete);
        if (totalBytes > LargestAllowed) return new(Guid.Empty, 0, totalBytes, UploadError.TooLarge);

        var session = new UploadSession
        {
            Id = Guid.CreateVersion7(),
            UserId = userId,
            MemoryId = memoryId,
            FileName = SafeName(fileName),
            TotalBytes = totalBytes,
            IsVoiceRecording = isVoice,
            CreatedAt = Now,
            UpdatedAt = Now,
        };
        Directory.CreateDirectory(options.UploadsDir);
        await File.WriteAllBytesAsync(storage.UploadPartPath(session.Id), []);
        db.UploadSessions.Add(session);
        await db.SaveChangesAsync();
        return new(session.Id, 0, totalBytes);
    }

    public async Task<UploadSession?> FindAsync(Guid uploadId, Guid userId) =>
        await db.UploadSessions.SingleOrDefaultAsync(s => s.Id == uploadId && s.UserId == userId && s.CompletedAt == null);

    public async Task<UploadStatus> AppendAsync(UploadSession session, long offset, Stream body, long? length, CancellationToken ct)
    {
        if (offset != session.ReceivedBytes)
            return new(session.Id, session.ReceivedBytes, session.TotalBytes, UploadError.WrongOffset);
        if (length is > MaxChunkBytes || offset + (length ?? 0) > session.TotalBytes)
            return new(session.Id, session.ReceivedBytes, session.TotalBytes, UploadError.TooLarge);

        var path = storage.UploadPartPath(session.Id);
        var limit = Math.Min(MaxChunkBytes, session.TotalBytes - offset);
        long written;
        await using (var file = new FileStream(path, FileMode.OpenOrCreate, FileAccess.Write, FileShare.None, 81920, useAsync: true))
        {
            file.SetLength(offset); // drop any half-written piece from an earlier, interrupted attempt
            file.Position = offset;
            written = await CopyLimitedAsync(body, file, limit, ct);
            if (written > limit)
            {
                file.SetLength(offset);
                return new(session.Id, session.ReceivedBytes, session.TotalBytes, UploadError.TooLarge);
            }
        }

        // Refuse unknown file types as soon as the first bytes arrive, not after a 1 GB upload.
        if (offset == 0)
        {
            var detected = await FileSignatures.DetectAsync(path);
            var kind = session.IsVoiceRecording ? AttachmentKind.Audio : detected?.Kind;
            if (detected is null || session.TotalBytes > LimitFor(kind!.Value))
            {
                await AbandonAsync(session);
                return new(session.Id, 0, session.TotalBytes, detected is null ? UploadError.Unsupported : UploadError.TooLarge);
            }
        }

        session.ReceivedBytes = offset + written;
        session.UpdatedAt = Now;
        await db.SaveChangesAsync(ct);
        return new(session.Id, session.ReceivedBytes, session.TotalBytes);
    }

    public async Task<UploadStatus> CompleteAsync(UploadSession session, Guid userId)
    {
        if (session.ReceivedBytes != session.TotalBytes)
            return new(session.Id, session.ReceivedBytes, session.TotalBytes, UploadError.Incomplete);

        var (attachment, error) = await StoreAsync(storage.UploadPartPath(session.Id), session.MemoryId, null, session.FileName,
            session.IsVoiceRecording, userId);
        if (attachment is null)
        {
            await AbandonAsync(session);
            return new(session.Id, session.ReceivedBytes, session.TotalBytes, error);
        }

        session.CompletedAt = Now;
        session.AttachmentId = attachment.Id;
        await db.SaveChangesAsync();
        jobs.EnqueueProcessing(attachment.Id);
        return new(session.Id, session.ReceivedBytes, session.TotalBytes, Attachment: attachment);
    }

    /// <summary>The no-JavaScript path: a normal multipart form upload.</summary>
    public Task<(Attachment? Attachment, UploadError Error)> AddFromStreamAsync(Guid memoryId, string fileName, Stream content, Guid userId) =>
        AddAsync(memoryId, null, fileName, content, userId);

    /// <summary>A file for an item in the family archive (D-080): same checks, storage and processing as a story's files.</summary>
    public Task<(Attachment? Attachment, UploadError Error)> AddToArchiveAsync(Guid archiveItemId, string fileName, Stream content, Guid userId) =>
        AddAsync(null, archiveItemId, fileName, content, userId);

    private async Task<(Attachment? Attachment, UploadError Error)> AddAsync(Guid? memoryId, Guid? archiveItemId, string fileName, Stream content, Guid userId)
    {
        Directory.CreateDirectory(options.UploadsDir);
        var temp = storage.UploadPartPath(Guid.CreateVersion7());
        await using (var file = File.Create(temp))
        {
            var written = await CopyLimitedAsync(content, file, LargestAllowed + 1, CancellationToken.None);
            if (written > LargestAllowed)
            {
                file.Close();
                File.Delete(temp);
                return (null, UploadError.TooLarge);
            }
        }

        var (attachment, error) = await StoreAsync(temp, memoryId, archiveItemId, SafeName(fileName), isVoice: false, userId);
        if (attachment is null) File.Delete(temp);
        else jobs.EnqueueProcessing(attachment.Id);
        return (attachment, error);
    }

    private async Task<(Attachment?, UploadError)> StoreAsync(string tempPath, Guid? memoryId, Guid? archiveItemId, string fileName, bool isVoice, Guid userId)
    {
        var detected = await FileSignatures.DetectAsync(tempPath);
        if (detected is null) return (null, UploadError.Unsupported);

        var kind = isVoice ? AttachmentKind.Audio : detected.Kind;
        var size = new FileInfo(tempPath).Length;
        if (size > LimitFor(kind)) return (null, UploadError.TooLarge);

        string sha;
        await using (var stream = File.OpenRead(tempPath))
            sha = Convert.ToHexStringLower(await SHA256.HashDataAsync(stream));

        var nextOrder = (await db.Attachments.Where(a => memoryId != null ? a.MemoryId == memoryId : a.ArchiveItemId == archiveItemId).MaxAsync(a => (int?)a.SortOrder) ?? 0) + 1;
        var attachment = new Attachment
        {
            Id = Guid.CreateVersion7(),
            MemoryId = memoryId,
            ArchiveItemId = archiveItemId,
            Kind = kind,
            IsVoiceRecording = isVoice,
            OriginalFileName = fileName,
            ContentType = detected.ContentType,
            Extension = detected.Extension,
            SizeBytes = size,
            Sha256 = sha,
            Caption = isVoice ? "Voice recording" : null,
            SortOrder = nextOrder,
            State = ProcessingState.Pending,
            UploadedById = userId,
            CreatedAt = Now,
        };

        Directory.CreateDirectory(storage.FolderFor(attachment));
        File.Move(tempPath, storage.PathFor(attachment, MediaVariant.Original));
        db.Attachments.Add(attachment);

        if (memoryId is not null)
        {
            // Adding a photo or recording counts as starting the story.
            var memory = await db.Memories.SingleAsync(m => m.Id == memoryId);
            if (memory.Status is MemoryStatus.NotStarted or MemoryStatus.Skipped) memory.Status = MemoryStatus.Draft;
            // Revision guards the words against stale tabs; adding a photo must not invalidate the page's autosave.
            memory.UpdatedAt = Now;
        }

        await db.SaveChangesAsync();
        return (attachment, UploadError.None);
    }

    public async Task<bool> SetCaptionAsync(Guid memoryId, Guid attachmentId, string? caption)
    {
        var a = await db.Attachments.SingleOrDefaultAsync(x => x.Id == attachmentId && x.MemoryId == memoryId && x.RemovedAt == null);
        if (a is null) return false;
        a.Caption = string.IsNullOrWhiteSpace(caption) ? null : caption.Trim()[..Math.Min(caption.Trim().Length, 1000)];
        await db.SaveChangesAsync();
        return true;
    }

    /// <summary>Moves an attachment one place earlier (-1) or later (+1), the button-based alternative to dragging.</summary>
    public async Task<bool> MoveAsync(Guid memoryId, Guid attachmentId, int direction)
    {
        var list = await db.Attachments.Where(a => a.MemoryId == memoryId && a.RemovedAt == null)
            .OrderBy(a => a.SortOrder).ThenBy(a => a.CreatedAt).ToListAsync();
        var index = list.FindIndex(a => a.Id == attachmentId);
        var target = index + Math.Sign(direction);
        if (index < 0 || target < 0 || target >= list.Count) return false;

        (list[index], list[target]) = (list[target], list[index]);
        for (var i = 0; i < list.Count; i++) list[i].SortOrder = i + 1;
        await db.SaveChangesAsync();
        return true;
    }

    /// <summary>Sets the complete order (used by drag-to-reorder). Ids not in the story are ignored.</summary>
    public async Task<bool> ReorderAsync(Guid memoryId, IReadOnlyList<Guid> orderedIds)
    {
        var list = await db.Attachments.Where(a => a.MemoryId == memoryId && a.RemovedAt == null).ToListAsync();
        var position = orderedIds.Select((id, i) => (id, i)).ToDictionary(x => x.id, x => x.i);
        var ordered = list.OrderBy(a => position.GetValueOrDefault(a.Id, int.MaxValue)).ThenBy(a => a.SortOrder).ToList();
        for (var i = 0; i < ordered.Count; i++) ordered[i].SortOrder = i + 1;
        await db.SaveChangesAsync();
        return true;
    }

    /// <summary>Takes it out of the story. The original file is kept (admins can bring it back).</summary>
    public async Task<bool> RemoveAsync(Guid memoryId, Guid attachmentId)
    {
        var a = await db.Attachments.SingleOrDefaultAsync(x => x.Id == attachmentId && x.MemoryId == memoryId && x.RemovedAt == null);
        if (a is null) return false;
        a.RemovedAt = Now;
        await db.SaveChangesAsync();
        return true;
    }

    private async Task AbandonAsync(UploadSession session)
    {
        try { File.Delete(storage.UploadPartPath(session.Id)); } catch (IOException) { }
        db.UploadSessions.Remove(session);
        await db.SaveChangesAsync();
    }

    private static async Task<long> CopyLimitedAsync(Stream source, Stream target, long limit, CancellationToken ct)
    {
        var buffer = new byte[81920];
        long total = 0;
        int read;
        while ((read = await source.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, limit - total + 1)), ct)) > 0)
        {
            total += read;
            if (total > limit) return total; // caller treats this as too large
            await target.WriteAsync(buffer.AsMemory(0, read), ct);
        }
        return total;
    }

    internal static string SafeName(string? name)
    {
        var n = Path.GetFileName(name ?? "").Trim();
        foreach (var c in Path.GetInvalidFileNameChars()) n = n.Replace(c, '_');
        if (n.Length == 0) n = "file";
        return n.Length > 200 ? n[..200] : n;
    }
}
