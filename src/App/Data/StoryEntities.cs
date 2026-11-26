namespace FamilyMemories.Data;

// ---------------------------------------------------------------------------
// Prompts
// ---------------------------------------------------------------------------

public sealed class Chapter
{
    public Guid Id { get; set; }
    /// <summary>Stable seed key ("roots"). Null for admin-created chapters.</summary>
    public string? Key { get; set; }
    public string Title { get; set; } = "";
    public int SortOrder { get; set; }
    /// <summary>Renamed or moved by an admin: the seed no longer overwrites its title or position.</summary>
    public bool EditedByAdmin { get; set; }
    public List<Prompt> Prompts { get; set; } = [];
}

public sealed class Prompt
{
    public Guid Id { get; set; }
    /// <summary>Stable seed key ("roots-born"). Null for admin-created and follow-up prompts.</summary>
    public string? Key { get; set; }
    /// <summary>Null only for one-off prompts written for a single person.</summary>
    public Guid? ChapterId { get; set; }
    public Chapter? Chapter { get; set; }
    public string Title { get; set; } = "";
    public string Question { get; set; } = "";
    public List<string> Hints { get; set; } = [];
    public string? SuggestedAttachments { get; set; }
    public bool IsCore { get; set; }
    public int SortOrder { get; set; }
    public bool IsActive { get; set; } = true;

    /// <summary>A one-off prompt for this person only ("custom prompt"), shown before their set.</summary>
    public Guid? ForUserId { get; set; }
    public AppUser? ForUser { get; set; }
    /// <summary>Who asked, shown above a one-off prompt ("Lily asked:").</summary>
    public Guid? AskedByUserId { get; set; }
    public AppUser? AskedByUser { get; set; }

    /// <summary>For "Ask a follow-up": the story that prompted the question.</summary>
    public Guid? FollowUpOfMemoryId { get; set; }
    /// <summary>When the storyteller was emailed about this follow-up (Phase 4).</summary>
    public DateTime? NotifiedAt { get; set; }

    /// <summary>Set once an admin edits the text; seed updates then leave it alone.</summary>
    public bool EditedByAdmin { get; set; }
    public DateTime CreatedAt { get; set; }
}

public enum PromptSetKind
{
    /// <summary>Every active prompt marked core (built in, follows the prompt list).</summary>
    Core = 1,
    /// <summary>Every active prompt (built in, follows the prompt list).</summary>
    Full = 2,
    /// <summary>Hand-picked by an admin.</summary>
    Custom = 3,
}

public sealed class PromptSet
{
    public Guid Id { get; set; }
    public string Name { get; set; } = "";
    public PromptSetKind Kind { get; set; }
    public List<PromptSetItem> Items { get; set; } = [];
    public DateTime CreatedAt { get; set; }
}

public sealed class PromptSetItem
{
    public Guid PromptSetId { get; set; }
    public Guid PromptId { get; set; }
    public Prompt? Prompt { get; set; }
}

public enum Pacing
{
    /// <summary>One new prompt unlocks each week (the default).</summary>
    Weekly = 1,
    /// <summary>Everything in the set is available now.</summary>
    AllAtOnce = 2,
}

/// <summary>Someone allowed to write stories on behalf of a storyteller ("interview mode").</summary>
public sealed class StorytellerHelper
{
    public Guid StorytellerId { get; set; }
    public AppUser? Storyteller { get; set; }
    public Guid HelperId { get; set; }
    public AppUser? Helper { get; set; }
    public DateTime CreatedAt { get; set; }
}

// ---------------------------------------------------------------------------
// Memories
// ---------------------------------------------------------------------------

public enum MemoryStatus { NotStarted = 0, Draft = 1, Complete = 2, Skipped = 3, Declined = 4 }

public enum Visibility { Private = 0, SelectedPeople = 1, AllFamily = 2, Link = 3 }

public sealed class Memory
{
    public Guid Id { get; set; }
    public Guid StorytellerId { get; set; }
    public AppUser? Storyteller { get; set; }
    /// <summary>Null for a free-form "Add a story".</summary>
    public Guid? PromptId { get; set; }
    public Prompt? Prompt { get; set; }

    public string Title { get; set; } = "";
    public string Body { get; set; } = "";
    public MemoryStatus Status { get; set; }
    public Visibility Visibility { get; set; } = Visibility.AllFamily;

    // Fuzzy date ("around 1965", "1970s", "June 1972", "summer after the war").
    public int? WhenYear { get; set; }
    public int? WhenMonth { get; set; }
    public int? WhenDay { get; set; }
    public bool WhenCirca { get; set; }
    public int? WhenDecade { get; set; }
    public string? WhenLabel { get; set; }
    public string? Place { get; set; }

    /// <summary>A story about someone in the tree (e.g. a grandparent who has died). Null for most stories.</summary>
    public Guid? AboutPersonId { get; set; }
    public Person? AboutPerson { get; set; }

    /// <summary>Set when someone else typed it for the storyteller (proxy entry).</summary>
    public Guid? WrittenById { get; set; }
    public AppUser? WrittenBy { get; set; }

    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
    /// <summary>When words or photos from an email reply were added (Phase 6); cleared when the story is finished.</summary>
    public DateTime? EmailReplyAt { get; set; }
    /// <summary>Bumped on every change, for optimistic concurrency between tabs and autosave.</summary>
    public int Revision { get; set; }

    public List<Attachment> Attachments { get; set; } = [];
    public List<MemoryVersion> Versions { get; set; } = [];

    public FuzzyDate When
    {
        get => new(WhenYear, WhenMonth, WhenDay, WhenCirca, WhenDecade, WhenLabel);
        set
        {
            WhenYear = value.Year; WhenMonth = value.Month; WhenDay = value.Day;
            WhenCirca = value.Circa; WhenDecade = value.Decade; WhenLabel = value.Label;
        }
    }
}

public enum VersionKind { Autosave = 1, Save = 2, Restore = 3 }

/// <summary>A saved copy of a memory's words. Every Save keeps one; autosaves are coalesced (D-018).</summary>
public sealed class MemoryVersion
{
    public Guid Id { get; set; }
    public Guid MemoryId { get; set; }
    public Memory? Memory { get; set; }
    public int Number { get; set; }
    public VersionKind Kind { get; set; }
    public string Title { get; set; } = "";
    public string Body { get; set; } = "";
    public MemoryStatus Status { get; set; }
    public int? WhenYear { get; set; }
    public int? WhenMonth { get; set; }
    public int? WhenDay { get; set; }
    public bool WhenCirca { get; set; }
    public int? WhenDecade { get; set; }
    public string? WhenLabel { get; set; }
    public string? Place { get; set; }
    public Guid SavedById { get; set; }
    public AppUser? SavedBy { get; set; }
    public DateTime SavedAt { get; set; }
}

// ---------------------------------------------------------------------------
// Attachments and uploads
// ---------------------------------------------------------------------------

public enum AttachmentKind { Photo = 1, Document = 2, Audio = 3, Video = 4 }

public enum ProcessingState { Pending = 0, Ready = 1, Failed = 2 }

public enum TranscriptState { None = 0, Pending = 1, Ready = 2, Approved = 3, Dismissed = 4, Failed = 5 }

public sealed class Attachment
{
    public Guid Id { get; set; }
    /// <summary>The story this belongs to; null for a file in the family archive (exactly one of the two is set).</summary>
    public Guid? MemoryId { get; set; }
    public Memory? Memory { get; set; }
    /// <summary>The archive item this belongs to (D-080).</summary>
    public Guid? ArchiveItemId { get; set; }
    public ArchiveItem? ArchiveItem { get; set; }
    public AttachmentKind Kind { get; set; }
    /// <summary>Recorded in the browser with "Record my voice".</summary>
    public bool IsVoiceRecording { get; set; }
    public string OriginalFileName { get; set; } = "";
    /// <summary>Detected from the file's bytes, not its name.</summary>
    public string ContentType { get; set; } = "";
    /// <summary>Extension used for the stored original, e.g. ".heic".</summary>
    public string Extension { get; set; } = "";
    public long SizeBytes { get; set; }
    public string Sha256 { get; set; } = "";
    public string? Caption { get; set; }
    public int SortOrder { get; set; }
    public int? Width { get; set; }
    public int? Height { get; set; }
    public double? DurationSeconds { get; set; }
    public ProcessingState State { get; set; }
    public string? ProcessingError { get; set; }
    public Guid UploadedById { get; set; }
    public DateTime CreatedAt { get; set; }
    /// <summary>What Whisper heard in a voice recording (Phase 6), for the storyteller to check before it joins the story.</summary>
    public string? Transcript { get; set; }
    public TranscriptState TranscriptState { get; set; }
    /// <summary>Removed from the story; the original stays on disk until an admin purges it.</summary>
    public DateTime? RemovedAt { get; set; }
    /// <summary>Keywords from the photo's own metadata (IPTC/XMP); hierarchical ones as "Family|Conlon|William".</summary>
    public List<string> Keywords { get; set; } = [];
    /// <summary>When the photo was taken, from its EXIF data, as the camera's wall-clock time (scans often carry the scan date instead).</summary>
    public DateTime? TakenAt { get; set; }
}

/// <summary>A resumable, chunked upload in progress. Bytes go to /data/uploads/{Id}.part.</summary>
public sealed class UploadSession
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public Guid MemoryId { get; set; }
    public string FileName { get; set; } = "";
    public long TotalBytes { get; set; }
    public long ReceivedBytes { get; set; }
    public bool IsVoiceRecording { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
    public Guid? AttachmentId { get; set; }
}

// ---------------------------------------------------------------------------
// Audit log
// ---------------------------------------------------------------------------

public sealed class AuditEntry
{
    public long Id { get; set; }
    public DateTime At { get; set; }
    public Guid? ActorId { get; set; }
    public string ActorName { get; set; } = "";
    /// <summary>Dotted verb, e.g. "user.invite", "memory.restore", "logincard.print".</summary>
    public string Action { get; set; } = "";
    public string? TargetType { get; set; }
    public string? TargetId { get; set; }
    public string Summary { get; set; } = "";
    public string? Ip { get; set; }
}

// ---------------------------------------------------------------------------
// Backups and settings
// ---------------------------------------------------------------------------

public enum BackupKind { Backup = 1, Verify = 2, Restore = 3 }
public enum BackupTrigger { Nightly = 1, Manual = 2, PreMigration = 3, PreRestore = 4, Weekly = 5 }
public enum BackupOutcome { Running = 0, Succeeded = 1, Failed = 2 }

/// <summary>One backup, verification or restore, shown on the admin dashboard.</summary>
public sealed class BackupRun
{
    public Guid Id { get; set; }
    public BackupKind Kind { get; set; }
    public BackupTrigger Trigger { get; set; }
    public BackupOutcome Outcome { get; set; }
    public DateTime StartedAt { get; set; }
    public DateTime? FinishedAt { get; set; }
    public string? SnapshotId { get; set; }
    public string? OffsiteSnapshotId { get; set; }
    public string Message { get; set; } = "";
    public Guid? StartedById { get; set; }
}

/// <summary>Small key/value settings changed from the admin UI (banner dismissals, SMTP, templates…).</summary>
public sealed class AppSetting
{
    public string Key { get; set; } = "";
    public string Value { get; set; } = "";
    public DateTime UpdatedAt { get; set; }
}

/// <summary>A short code in a reminder's reply-to address (memories+CODE@…), linking a reply to its person and question.</summary>
public sealed class ReplyCode
{
    public string Code { get; set; } = "";
    public Guid UserId { get; set; }
    public Guid PromptId { get; set; }
    public DateTime CreatedAt { get; set; }
}

public enum SourceKind { EmailReply = 1, VoiceTranscript = 2, PaperScan = 3, Import = 4, Pasted = 5 }

/// <summary>
/// Raw material behind a story, kept as it arrived: an email reply, what speech-to-text heard before anyone fixed it, a
/// transcription pasted in, an imported spreadsheet cell. The story itself is the edited, finished version (D-079).
/// Only people who can edit the story see these.
/// </summary>
public sealed class MemorySource
{
    public Guid Id { get; set; }
    public Guid MemoryId { get; set; }
    public Memory? Memory { get; set; }
    public SourceKind Kind { get; set; }
    public string Text { get; set; } = "";
    /// <summary>The recording or scan it came from, when there is one.</summary>
    public Guid? AttachmentId { get; set; }
    public Guid? CreatedById { get; set; }
    public AppUser? CreatedBy { get; set; }
    public DateTime CreatedAt { get; set; }
}

/// <summary>A folder in the family archive; folders can hold folders (D-080).</summary>
public sealed class Collection
{
    public Guid Id { get; set; }
    public Guid? ParentId { get; set; }
    public Collection? Parent { get; set; }
    public string Title { get; set; } = "";
    public string? Description { get; set; }
    public int SortOrder { get; set; }
    public DateTime CreatedAt { get; set; }
}

/// <summary>
/// Something kept in the family archive: a letter, a certificate, an album page, a recording. One or more files (scans of
/// each page), what it says (a transcription), when and where it's from, and where the original is now (D-080).
/// </summary>
public sealed class ArchiveItem
{
    public Guid Id { get; set; }
    /// <summary>The folder it's in; null for the top of the archive.</summary>
    public Guid? CollectionId { get; set; }
    public Collection? Collection { get; set; }
    public string Title { get; set; } = "";
    public string? Description { get; set; }
    public int? WhenYear { get; set; }
    public int? WhenMonth { get; set; }
    public int? WhenDay { get; set; }
    public bool WhenCirca { get; set; }
    public int? WhenDecade { get; set; }
    public string? WhenLabel { get; set; }
    public string? Place { get; set; }
    public string? Transcription { get; set; }
    /// <summary>Where the original is now ("Aunt Ann's cedar chest").</summary>
    public string? Provenance { get; set; }
    public Guid? CreatedById { get; set; }
    public AppUser? CreatedBy { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public List<Attachment> Files { get; set; } = [];

    public FuzzyDate When
    {
        get => new(WhenYear, WhenMonth, WhenDay, WhenCirca, WhenDecade, WhenLabel);
        set => (WhenYear, WhenMonth, WhenDay, WhenCirca, WhenDecade, WhenLabel) = (value.Year, value.Month, value.Day, value.Circa, value.Decade, value.Label);
    }
}

/// <summary>"Related documents": an archive item linked to a story.</summary>
public sealed class StoryDocument
{
    public Guid MemoryId { get; set; }
    public Memory? Memory { get; set; }
    public Guid ArchiveItemId { get; set; }
    public ArchiveItem? ArchiveItem { get; set; }
}
