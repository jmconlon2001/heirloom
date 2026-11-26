using FamilyMemories.Data;
using FamilyMemories.Family;
using Microsoft.EntityFrameworkCore;

namespace FamilyMemories.Exports;

/// <summary>Which stories go into an export (§9). Defaults: everyone, every chapter, finished stories, no private ones.</summary>
public sealed record ExportScope
{
    public List<Guid> StorytellerIds { get; init; } = [];
    public List<Guid> ChapterIds { get; init; } = [];
    public List<MemoryStatus> Statuses { get; init; } = [MemoryStatus.Complete];
    /// <summary>Private stories are left out unless an admin ticks this and confirms.</summary>
    public bool IncludePrivate { get; init; }
    public int? FromYear { get; init; }
    public int? ToYear { get; init; }
}

public enum TrimSize { Size6x9, Size8x10, Size8_5x11 }
public enum OrganizeBy { Storyteller, Chapter }

public sealed record BookOptions
{
    public string Title { get; init; } = "Our Family Stories";
    public string? Subtitle { get; init; }
    public string? Dedication { get; init; }
    public TrimSize Trim { get; init; } = TrimSize.Size6x9;
    public bool Bleed { get; init; }
    /// <summary>Pages per inch of the chosen paper (for the spine).</summary>
    public double PagesPerInch { get; init; } = SpineCalculator.Presets["kdp-white"].PagesPerInch;
    public string PaperPreset { get; init; } = "kdp-white";
    /// <summary>Printers need the page count to be a multiple of this (Lulu/KDP: 2; some offset printers: 4 or 16).</summary>
    public int PageMultiple { get; init; } = 2;
    public Guid? FrontPhotoId { get; init; }
    public Guid? BackPhotoId { get; init; }
    public string? BackText { get; init; }
    public OrganizeBy Organize { get; init; } = OrganizeBy.Storyteller;
}

public sealed record ExportPhoto(Attachment Attachment, string? Caption, IReadOnlyList<string> People);

public sealed record ExportClip(Attachment Attachment, string? Caption, string? ClipUrl);

public sealed record ExportStory(
    Memory Memory,
    string Slug,
    string Teller,
    Guid TellerId,
    string? WrittenBy,
    string? Question,
    string? ChapterTitle,
    int ChapterOrder,
    int PromptOrder,
    string When,
    IReadOnlyList<FamilyMemories.Stories.StoryBlock> Blocks,
    IReadOnlyList<ExportPhoto> Photos,
    IReadOnlyList<ExportClip> Clips,
    IReadOnlyList<Attachment> Documents,
    IReadOnlyList<Person> People)
{
    public bool HasWords => Blocks.Any(b => b.IsText);
    private HashSet<Guid> Placed => [.. Blocks.Where(b => b.MediaId is not null).Select(b => b.MediaId!.Value)];
    /// <summary>Photos and clips not placed in the text: they follow it, as before.</summary>
    public IEnumerable<ExportPhoto> UnplacedPhotos => Photos.Where(p => !Placed.Contains(p.Attachment.Id));
    public IEnumerable<ExportClip> UnplacedClips => Clips.Where(c => !Placed.Contains(c.Attachment.Id));
    public ExportPhoto? PhotoAt(Guid? id) => id is null ? null : Photos.FirstOrDefault(p => p.Attachment.Id == id);
    public ExportClip? ClipAt(Guid? id) => id is null ? null : Clips.FirstOrDefault(c => c.Attachment.Id == id);
}

public sealed record ExportSection(string Id, string Title, IReadOnlyList<ExportStory> Stories);

public sealed record ExportPerson(Person Person, string Slug, string LifeSpan, IReadOnlyList<ExportStory> Stories);

public sealed record ExportContent(
    string AppName,
    IReadOnlyList<ExportStory> Stories,
    IReadOnlyList<ExportSection> Sections,
    IReadOnlyList<ExportPerson> People,
    IReadOnlyList<Person> AllPeople,
    IReadOnlyList<ParentChild> Parents,
    IReadOnlyList<Partnership> Partnerships,
    DateTime CreatedAt)
{
    /// <summary>The website export has an archive page (D-080).</summary>
    public bool HasArchive { get; init; }
}

/// <summary>Gathers the stories, media and people for an export, applying the scope.</summary>
public sealed class ExportContentBuilder(AppDbContext db)
{
    public IQueryable<Memory> Query(ExportScope scope)
    {
        var q = db.Memories.AsNoTracking().Where(m => scope.Statuses.Contains(m.Status));
        if (!scope.IncludePrivate) q = q.Where(m => m.Visibility != Visibility.Private);
        if (scope.StorytellerIds.Count > 0) q = q.Where(m => scope.StorytellerIds.Contains(m.StorytellerId));
        if (scope.ChapterIds.Count > 0) q = q.Where(m => m.Prompt != null && scope.ChapterIds.Contains(m.Prompt.ChapterId!.Value));
        return q;
    }

    public async Task<ExportContent> BuildAsync(ExportScope scope, OrganizeBy organize, string appName, DateTime now,
        Func<Attachment, Task<string?>>? clipUrl = null)
    {
        var memories = await Query(scope)
            .Include(m => m.Storyteller).Include(m => m.WrittenBy).Include(m => m.Prompt).ThenInclude(p => p!.Chapter)
            .ToListAsync();
        // Year range uses the fuzzy date (undated stories are kept).
        memories = [.. memories.Where(m =>
            m.When.SortDate is not { } d || ((scope.FromYear is null || d.Year >= scope.FromYear) && (scope.ToYear is null || d.Year <= scope.ToYear)))];

        var ids = memories.Select(m => m.Id).ToList();
        var attachments = await db.Attachments.AsNoTracking()
            .Where(a => a.MemoryId != null && ids.Contains(a.MemoryId.Value) && a.RemovedAt == null && a.State == ProcessingState.Ready)
            .OrderBy(a => a.SortOrder).ThenBy(a => a.CreatedAt).ToListAsync();
        var attachmentIds = attachments.Select(a => a.Id).ToList();
        var photoPeople = (await db.AttachmentPeople.AsNoTracking().Include(x => x.Person).Where(x => attachmentIds.Contains(x.AttachmentId)).ToListAsync())
            .GroupBy(x => x.AttachmentId).ToDictionary(g => g.Key, g => g.Select(x => x.Person!.DisplayName).ToList());
        var storyPeople = (await db.MemoryPeople.AsNoTracking().Include(x => x.Person).Where(x => ids.Contains(x.MemoryId)).ToListAsync())
            .GroupBy(x => x.MemoryId).ToDictionary(g => g.Key, g => g.Select(x => x.Person!).ToList());

        var stories = new List<ExportStory>();
        foreach (var m in memories)
        {
            var mine = attachments.Where(a => a.MemoryId == m.Id).ToList();
            var clips = new List<ExportClip>();
            foreach (var a in mine.Where(a => a.Kind is AttachmentKind.Audio or AttachmentKind.Video))
                clips.Add(new ExportClip(a, Caption(a), clipUrl is null ? null : await clipUrl(a)));
            stories.Add(new ExportStory(
                m,
                Slug(m.Title, m.Id),
                m.Storyteller!.DisplayName,
                m.StorytellerId,
                m.WrittenBy?.DisplayName,
                m.Prompt?.Question,
                m.Prompt?.Chapter?.Title,
                m.Prompt?.Chapter?.SortOrder ?? int.MaxValue,
                m.Prompt?.SortOrder ?? int.MaxValue,
                m.When.Format(),
                FamilyMemories.Stories.StoryMarkup.Parse(m.Body),
                [.. mine.Where(a => a.Kind == AttachmentKind.Photo).Select(a => new ExportPhoto(a, Caption(a), photoPeople.GetValueOrDefault(a.Id) ?? []))],
                clips,
                [.. mine.Where(a => a.Kind == AttachmentKind.Document)],
                storyPeople.GetValueOrDefault(m.Id) ?? []));
        }
        stories = [.. stories.OrderBy(s => s.ChapterOrder).ThenBy(s => s.PromptOrder).ThenBy(s => s.Memory.When.SortDate ?? DateOnly.MaxValue).ThenBy(s => s.Memory.CreatedAt)];

        var sections = organize == OrganizeBy.Chapter
            ? stories.GroupBy(s => s.ChapterTitle ?? "More stories").Select((g, i) => new ExportSection($"section-{i + 1}", g.Key, [.. g])).ToList()
            : stories.GroupBy(s => (s.TellerId, s.Teller)).OrderBy(g => g.Key.Teller)
                .Select((g, i) => new ExportSection($"section-{i + 1}", $"Stories told by {g.Key.Teller}", [.. g])).ToList();

        var allPeople = await db.People.AsNoTracking().OrderBy(p => p.Surname).ThenBy(p => p.GivenNames).ToListAsync();
        var parents = await db.ParentChildren.AsNoTracking().ToListAsync();
        var partners = await db.Partnerships.AsNoTracking().ToListAsync();
        // Exports leave the app, so living people's birth dates are year-only, like for non-admins (§7).
        var people = allPeople.Select(p => new ExportPerson(p, Slug(p.DisplayName, p.Id), PeopleService.LifeSpan(p, isAdmin: false),
                [.. stories.Where(s => s.People.Any(x => x.Id == p.Id) || s.Memory.AboutPersonId == p.Id || (p.LinkedUserId == s.TellerId))]))
            .ToList();

        return new ExportContent(appName, stories, sections, people, allPeople, parents, partners, now);
    }

    private static string? Caption(Attachment a) =>
        a.IsVoiceRecording && (a.Caption is null || a.Caption == "Voice recording") ? "A recording in their own voice" : a.Caption;

    /// <summary>"the-day-i-met-joe-1a2b3c" — readable, unique, safe for file names.</summary>
    public static string Slug(string? title, Guid id)
    {
        var chars = (title ?? "").ToLowerInvariant().Select(c => char.IsLetterOrDigit(c) ? c : '-').ToArray();
        var slug = string.Join('-', new string(chars).Split('-', StringSplitOptions.RemoveEmptyEntries));
        if (slug.Length > 50) slug = slug[..50].TrimEnd('-');
        return (slug.Length == 0 ? "story" : slug) + "-" + id.ToString("N")[^6..];
    }
}
