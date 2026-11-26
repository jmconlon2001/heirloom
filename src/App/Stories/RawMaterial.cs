using FamilyMemories.Data;

namespace FamilyMemories.Stories;

/// <summary>Keeps raw material next to a story (D-079). Added to the caller's unit of work; the caller saves.</summary>
public static class RawMaterial
{
    public const int MaxLength = 200_000;

    public static void Keep(AppDbContext db, Guid memoryId, SourceKind kind, string? text, Guid? createdBy, DateTime now, Guid? attachmentId = null)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        text = text.Replace("\r\n", "\n").Trim();
        db.MemorySources.Add(new MemorySource
        {
            Id = Guid.CreateVersion7(), MemoryId = memoryId, Kind = kind,
            Text = text.Length > MaxLength ? text[..MaxLength] : text,
            AttachmentId = attachmentId, CreatedById = createdBy, CreatedAt = now,
        });
    }

    public static string Label(SourceKind kind) => kind switch
    {
        SourceKind.EmailReply => "Email reply, as it arrived",
        SourceKind.VoiceTranscript => "What speech-to-text heard, before any fixes",
        SourceKind.PaperScan => "Transcription of a letter or paper page",
        SourceKind.Import => "Imported from a spreadsheet",
        _ => "Pasted in",
    };
}
