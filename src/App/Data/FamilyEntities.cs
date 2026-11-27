namespace FamilyMemories.Data;

// ---------------------------------------------------------------------------
// Family tree
// ---------------------------------------------------------------------------

public enum Sex { Unknown = 0, Female = 1, Male = 2, Other = 3 }

/// <summary>Someone in the family tree. May be deceased, and may (or may not) be a person who signs in.</summary>
public sealed class Person
{
    public Guid Id { get; set; }
    public string GivenNames { get; set; } = "";
    public string Surname { get; set; } = "";
    /// <summary>Surname at birth (maiden name).</summary>
    public string? BirthSurname { get; set; }
    public string? Nickname { get; set; }
    public Sex Sex { get; set; }

    public int? BirthYear { get; set; }
    public int? BirthMonth { get; set; }
    public int? BirthDay { get; set; }
    public bool BirthCirca { get; set; }
    public string? BirthPlace { get; set; }

    public bool IsLiving { get; set; } = true;
    public int? DeathYear { get; set; }
    public int? DeathMonth { get; set; }
    public int? DeathDay { get; set; }
    public bool DeathCirca { get; set; }
    public string? DeathPlace { get; set; }

    public string? Bio { get; set; }
    /// <summary>Profile photo: any photo attachment.</summary>
    public Guid? PhotoAttachmentId { get; set; }
    public Attachment? PhotoAttachment { get; set; }
    /// <summary>The account this person signs in with, if any.</summary>
    public Guid? LinkedUserId { get; set; }
    public AppUser? LinkedUser { get; set; }
    /// <summary>The @I12@ id from a GEDCOM import, so re-imports can be matched.</summary>
    public string? GedcomId { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    public string DisplayName => string.Join(' ', new[] { GivenNames, Surname }.Where(s => !string.IsNullOrWhiteSpace(s))).Trim() is { Length: > 0 } n ? n : "Unnamed";

    public FuzzyDate Birth => new(BirthYear, BirthMonth, BirthDay, BirthCirca, null, null);
    public FuzzyDate Death => new(DeathYear, DeathMonth, DeathDay, DeathCirca, null, null);
}

public enum ParentKind { Biological = 1, Adoptive = 2, Step = 3 }

public sealed class ParentChild
{
    public Guid ParentId { get; set; }
    public Person? Parent { get; set; }
    public Guid ChildId { get; set; }
    public Person? Child { get; set; }
    public ParentKind Kind { get; set; } = ParentKind.Biological;
}

public enum PartnershipKind { Married = 1, Partnered = 2, Divorced = 3 }

public sealed class Partnership
{
    public Guid Id { get; set; }
    public Guid Person1Id { get; set; }
    public Person? Person1 { get; set; }
    public Guid Person2Id { get; set; }
    public Person? Person2 { get; set; }
    public PartnershipKind Kind { get; set; } = PartnershipKind.Married;
    public int? StartYear { get; set; }
    public int? EndYear { get; set; }
}

/// <summary>"People in this story".</summary>
public sealed class MemoryPerson
{
    public Guid MemoryId { get; set; }
    public Memory? Memory { get; set; }
    public Guid PersonId { get; set; }
    public Person? Person { get; set; }
}

/// <summary>"Who's in this photo".</summary>
public sealed class AttachmentPerson
{
    public Guid AttachmentId { get; set; }
    public Attachment? Attachment { get; set; }
    public Guid PersonId { get; set; }
    public Person? Person { get; set; }
}

// ---------------------------------------------------------------------------
// Sharing and conversation
// ---------------------------------------------------------------------------

/// <summary>For <see cref="Visibility.SelectedPeople"/>: who may read it.</summary>
public sealed class MemoryShare
{
    public Guid MemoryId { get; set; }
    public Guid UserId { get; set; }
    public AppUser? User { get; set; }
}

/// <summary>A read-only link for relatives without accounts. Only the token's hash is stored.</summary>
public sealed class LinkShare
{
    public Guid Id { get; set; }
    public Guid MemoryId { get; set; }
    /// <summary>
    /// A "clip link": opens just this recording or video (the QR codes printed in a PDF or book).
    /// Clip links work whatever the story's visibility, until revoked.
    /// </summary>
    public Guid? AttachmentId { get; set; }
    public Memory? Memory { get; set; }
    public required byte[] TokenHash { get; set; }
    /// <summary>PBKDF2 hash of an optional passcode.</summary>
    public string? PasscodeHash { get; set; }
    public string? Label { get; set; }
    public DateTime? ExpiresAt { get; set; }
    public DateTime? RevokedAt { get; set; }
    public Guid CreatedById { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? LastUsedAt { get; set; }
    public int ViewCount { get; set; }
}

public sealed class MemoryReaction
{
    public Guid MemoryId { get; set; }
    public Guid UserId { get; set; }
    public AppUser? User { get; set; }
    public DateTime CreatedAt { get; set; }
}

public sealed class MemoryComment
{
    public Guid Id { get; set; }
    public Guid MemoryId { get; set; }
    public Guid UserId { get; set; }
    public AppUser? User { get; set; }
    public string Body { get; set; } = "";
    public DateTime CreatedAt { get; set; }
    public DateTime? DeletedAt { get; set; }
}

/// <summary>First time each person read a story ("3 family members read your story", Phase 4).</summary>
public sealed class MemoryRead
{
    public Guid MemoryId { get; set; }
    public Guid UserId { get; set; }
    public DateTime FirstReadAt { get; set; }
    public DateTime? NotifiedAt { get; set; }
}

// ---------------------------------------------------------------------------
// Exports
// ---------------------------------------------------------------------------

public enum ExportKind { Website = 1, Pdf = 2, Book = 3, Archive = 4 }
public enum ExportStatus { Queued = 0, Running = 1, Done = 2, Failed = 3 }

/// <summary>One export (website zip, shareable PDF, book package, portable archive), kept under /data/exports.</summary>
public sealed class ExportJob
{
    public Guid Id { get; set; }
    public ExportKind Kind { get; set; }
    /// <summary>1, 2, 3… per kind, so files are versioned ("family-book-v3.zip").</summary>
    public int Version { get; set; }
    public ExportStatus Status { get; set; }
    public int Progress { get; set; }
    public string Message { get; set; } = "";
    public string ScopeJson { get; set; } = "{}";
    public string OptionsJson { get; set; } = "{}";
    public string? FileName { get; set; }
    public long? SizeBytes { get; set; }
    public Guid CreatedById { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? FinishedAt { get; set; }
}

/// <summary>Where a fact comes from: a source in the family's genealogy program (a census, a certificate, a book).</summary>
public sealed record FactCitation(string Title, string? Page = null, string? Url = null);

/// <summary>
/// A life event or fact about a person (residence, occupation, military service, immigration, burial…), usually read from
/// the family's main genealogy program's GEDCOM file, with its sources. Heirloom shows these; the genealogy program owns them.
/// </summary>
public sealed class PersonFact
{
    public Guid Id { get; set; }
    public Guid PersonId { get; set; }
    public Person? Person { get; set; }
    /// <summary>The GEDCOM tag (RESI, OCCU, MILI…), "EVEN" for a custom event, "NOTE" for a note, "SOUR" for sources about the whole person.</summary>
    public string Kind { get; set; } = "";
    /// <summary>What people read: "Residence", "Occupation", or a custom event's own name.</summary>
    public string Label { get; set; } = "";
    public int? Year { get; set; }
    public int? Month { get; set; }
    public int? Day { get; set; }
    public bool Circa { get; set; }
    /// <summary>For a couple's event (marriage, divorce…): the other partner. Names can't tell two "(Living)" partners apart.</summary>
    public Guid? PartnerId { get; set; }
    /// <summary>A date kept as words: "June 16" with no year, or a date phrase the file gave.</summary>
    public string? DateWords { get; set; }
    public string? Place { get; set; }
    public string? Value { get; set; }
    public string? Note { get; set; }
    public List<FactCitation> Citations { get; set; } = [];
    public int SortOrder { get; set; }
    /// <summary>Came from a GEDCOM import; replaced when the main tree is imported again.</summary>
    public bool FromGedcom { get; set; }

    public FuzzyDate Date => new(Year, Month, Day, Circa, null, DateWords);
}

/// <summary>A link from a person to somewhere else about them: the family's tree website, FamilySearch, Find a Grave, a memorial page.</summary>
public sealed class PersonLink
{
    public Guid Id { get; set; }
    public Guid PersonId { get; set; }
    public Person? Person { get; set; }
    public string Label { get; set; } = "";
    public string Url { get; set; } = "";
    public int SortOrder { get; set; }
    public bool FromGedcom { get; set; }
}
