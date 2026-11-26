using FamilyMemories.Auth;
using FamilyMemories.Data;
using FamilyMemories.Media;
using FamilyMemories.Stories;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace FamilyMemories.Pages.Stories;

/// <summary>
/// One prompt per screen. Works as a plain form (Save posts everything, files included);
/// answer.js adds 5-second autosave, chunked uploads with progress, and voice recording.
/// </summary>
[DisableRequestSizeLimit]
public sealed class WriteModel(
    AppDbContext db,
    StoryAccess access,
    MemoryService memories,
    AttachmentService attachments,
    StoryPlanService plans,
    FamilyMemories.Family.PeopleService people,
    FamilyMemories.Family.SharingService sharing,
    TimeProvider clock) : PageModel
{
    public Memory Memory { get; private set; } = null!;
    public Prompt? Prompt => Memory.Prompt;
    public AppUser Storyteller => Memory.Storyteller!;
    public List<Attachment> Items { get; private set; } = [];
    public bool IsProxy => User.UserId() != Memory.StorytellerId;

    [BindProperty] public string? Title { get; set; }
    [BindProperty] public string? Body { get; set; }
    [BindProperty] public string? Place { get; set; }
    [BindProperty] public int? WhenYear { get; set; }
    [BindProperty] public int? WhenMonth { get; set; }
    [BindProperty] public int? WhenDay { get; set; }
    [BindProperty] public bool WhenCirca { get; set; }
    [BindProperty] public int? WhenDecade { get; set; }
    [BindProperty] public string? WhenLabel { get; set; }
    [BindProperty] public Dictionary<Guid, string?> Captions { get; set; } = [];
    /// <summary>Null when the form didn't include the picker; then the visibility is left as it is.</summary>
    [BindProperty] public Visibility? Visibility { get; set; }
    [BindProperty] public List<Guid> SharedWith { get; set; } = [];
    [BindProperty] public bool PeopleFormPresent { get; set; }
    [BindProperty] public List<Guid> PeopleIds { get; set; } = [];
    [BindProperty] public string? AddPerson { get; set; }
    [BindProperty] public List<Guid> PhotoFormPresent { get; set; } = [];
    [BindProperty] public Dictionary<Guid, List<Guid>> PhotoPeople { get; set; } = [];
    [BindProperty] public Dictionary<Guid, string?> PhotoAddPerson { get; set; } = [];
    [BindProperty] public Dictionary<Guid, string?> Transcripts { get; set; } = [];

    public List<AppUser> FamilyUsers { get; private set; } = [];
    public Dictionary<Guid, Guid> UserPhotos { get; private set; } = [];
    public List<Person> AllPeople { get; private set; } = [];
    public List<Person> TaggedPeople { get; private set; } = [];
    public Dictionary<Guid, List<Person>> PhotoTags { get; private set; } = [];

    public string? WriterName { get; private set; }
    public int CurrentYear => clock.GetUtcNow().Year;

    private async Task<bool> LoadAsync(Guid id)
    {
        var memory = await db.Memories
            .Include(m => m.Prompt).ThenInclude(p => p!.Chapter)
            .Include(m => m.Prompt).ThenInclude(p => p!.AskedByUser)
            .Include(m => m.Storyteller)
            .Include(m => m.WrittenBy)
            .Include(m => m.AboutPerson)
            .AsNoTracking()
            .SingleOrDefaultAsync(m => m.Id == id);
        if (memory is null || !await access.CanEditAsync(User, memory)) return false;

        Memory = memory;
        Items = await db.Attachments.AsNoTracking()
            .Where(a => a.MemoryId == id && a.RemovedAt == null)
            .OrderBy(a => a.SortOrder).ThenBy(a => a.CreatedAt).ToListAsync();
        WriterName = memory.WrittenBy?.DisplayName ?? (IsProxy ? User.DisplayName() : null);

        FamilyUsers = await db.Users.AsNoTracking().Where(u => u.Id != memory.StorytellerId).OrderBy(u => u.DisplayName).ToListAsync();
        UserPhotos = await db.People.AsNoTracking().Where(p => p.LinkedUserId != null && p.PhotoAttachmentId != null)
            .ToDictionaryAsync(p => p.LinkedUserId!.Value, p => p.PhotoAttachmentId!.Value);
        AllPeople = await db.People.AsNoTracking().OrderBy(p => p.GivenNames).ThenBy(p => p.Surname).ToListAsync();
        TaggedPeople = await db.MemoryPeople.AsNoTracking().Where(x => x.MemoryId == id).Select(x => x.Person!).OrderBy(p => p.GivenNames).ToListAsync();
        var itemIds = Items.Select(a => a.Id).ToList();
        PhotoTags = (await db.AttachmentPeople.AsNoTracking().Where(x => itemIds.Contains(x.AttachmentId)).Include(x => x.Person).ToListAsync())
            .GroupBy(x => x.AttachmentId).ToDictionary(g => g.Key, g => g.Select(x => x.Person!).ToList());
        return true;
    }

    public async Task<IActionResult> OnGetAsync(Guid id)
    {
        if (!await LoadAsync(id)) return NotFound();
        Title = Memory.PromptId is null ? Memory.Title : null;
        Body = Memory.Body;
        Place = Memory.Place;
        (WhenYear, WhenMonth, WhenDay, WhenCirca, WhenDecade, WhenLabel) =
            (Memory.WhenYear, Memory.WhenMonth, Memory.WhenDay, Memory.WhenCirca, Memory.WhenDecade, Memory.WhenLabel);
        Visibility = Memory.Visibility;
        SharedWith = await db.MemoryShares.Where(x => x.MemoryId == id).Select(x => x.UserId).ToListAsync();
        Response.Headers.CacheControl = "no-store";
        return Page();
    }

    public async Task<IActionResult> OnPostSaveAsync(Guid id)
    {
        if (!await LoadAsync(id)) return NotFound();
        var problems = await SaveFormAsync();
        TempData["Flash"] = problems ?? $"Saved at {LocalTime()}.";
        return LocalRedirect($"/stories/{id}/write");
    }

    public async Task<IActionResult> OnPostFinishAsync(Guid id)
    {
        if (!await LoadAsync(id)) return NotFound();
        var problems = await SaveFormAsync();
        await memories.SetStatusAsync(id, MemoryStatus.Complete, User.UserId());
        if (problems is not null) TempData["Flash"] = problems;
        return LocalRedirect($"/stories/{id}/done");
    }

    public async Task<IActionResult> OnPostSkipAsync(Guid id)
    {
        if (!await LoadAsync(id)) return NotFound();
        await SaveFormAsync();
        await memories.SetStatusAsync(id, MemoryStatus.Skipped, User.UserId());
        TempData["Flash"] = "That's fine. We've saved this question for later.";
        return await NextAsync();
    }

    public async Task<IActionResult> OnPostDeclineAsync(Guid id)
    {
        if (!await LoadAsync(id)) return NotFound();
        await SaveFormAsync();
        await memories.SetStatusAsync(id, MemoryStatus.Declined, User.UserId());
        TempData["Flash"] = "That's perfectly fine. We won't ask this one again. You can still find it under \"See all my questions\" if you change your mind.";
        return await NextAsync();
    }

    public async Task<IActionResult> OnPostMoveAsync(Guid id, Guid attachmentId, int direction)
    {
        if (!await LoadAsync(id)) return NotFound();
        await SaveFormAsync();
        await attachments.MoveAsync(id, attachmentId, direction);
        return LocalRedirect($"/stories/{id}/write#attachment-{attachmentId}");
    }

    public async Task<IActionResult> OnPostRemoveAsync(Guid id, Guid attachmentId)
    {
        if (!await LoadAsync(id)) return NotFound();
        await SaveFormAsync();
        if (await attachments.RemoveAsync(id, attachmentId)) TempData["Removed"] = attachmentId.ToString();
        return LocalRedirect($"/stories/{id}/write#attachments");
    }

    /// <summary>Speech-to-text (Phase 6): the storyteller's checked words are added as a new paragraph.</summary>
    public async Task<IActionResult> OnPostUseTranscriptAsync(Guid id, Guid attachmentId)
    {
        if (!await LoadAsync(id)) return NotFound();
        var text = Transcripts.GetValueOrDefault(attachmentId)?.Trim();
        if (!string.IsNullOrEmpty(text))
            Body = string.IsNullOrWhiteSpace(Body) ? text : Body.TrimEnd() + "\n\n" + text;
        await SaveFormAsync();
        await db.Attachments.Where(a => a.Id == attachmentId && a.MemoryId == id)
            .ExecuteUpdateAsync(s => s.SetProperty(a => a.TranscriptState, TranscriptState.Approved).SetProperty(a => a.Transcript, text));
        TempData["Flash"] = "Your recorded words have been added to the story.";
        return LocalRedirect($"/stories/{id}/write");
    }

    public async Task<IActionResult> OnPostDismissTranscriptAsync(Guid id, Guid attachmentId)
    {
        if (!await LoadAsync(id)) return NotFound();
        await SaveFormAsync();
        await db.Attachments.Where(a => a.Id == attachmentId && a.MemoryId == id)
            .ExecuteUpdateAsync(s => s.SetProperty(a => a.TranscriptState, TranscriptState.Dismissed));
        return LocalRedirect($"/stories/{id}/write#attachment-{attachmentId}");
    }

    public async Task<IActionResult> OnPostUndoRemoveAsync(Guid id, Guid attachmentId)
    {
        if (!await LoadAsync(id)) return NotFound();
        var a = await db.Attachments.SingleOrDefaultAsync(x => x.Id == attachmentId && x.MemoryId == id);
        if (a is not null)
        {
            a.RemovedAt = null;
            await db.SaveChangesAsync();
        }
        return LocalRedirect($"/stories/{id}/write#attachment-{attachmentId}");
    }

    /// <summary>Saves words, date, place, captions and any files posted with the form. Returns a message if some files were refused.</summary>
    private async Task<string?> SaveFormAsync()
    {
        var when = FuzzyDate.Normalize(WhenYear, WhenMonth, WhenDay, WhenCirca, WhenDecade, WhenLabel, CurrentYear);
        await memories.SaveAsync(Memory.Id, new MemoryEdit(Title, Body, when, Place), User.UserId(), VersionKind.Save);

        foreach (var (attachmentId, caption) in Captions)
            if (Items.Any(a => a.Id == attachmentId && a.Caption != caption))
                await attachments.SetCaptionAsync(Memory.Id, attachmentId, caption);

        // Who can read it, and who is in it.
        if (Visibility is { } visibility) await sharing.SetVisibilityAsync(Memory.Id, visibility, SharedWith);
        var unknown = new List<string>();
        if (PeopleFormPresent)
        {
            var ids = new List<Guid>(PeopleIds);
            if (!string.IsNullOrWhiteSpace(AddPerson))
            {
                if (await people.FindByNameAsync(AddPerson) is { } found) ids.Add(found.Id);
                else unknown.Add(AddPerson.Trim());
            }
            if (Memory.AboutPersonId is { } aboutId && !ids.Contains(aboutId)) ids.Add(aboutId);
            await people.SetMemoryPeopleAsync(Memory.Id, ids);
        }
        foreach (var attachmentId in PhotoFormPresent.Where(a => Items.Any(i => i.Id == a)))
        {
            var wanted = PhotoPeople.GetValueOrDefault(attachmentId) ?? [];
            if (PhotoAddPerson.GetValueOrDefault(attachmentId) is { Length: > 0 } typed)
            {
                if (await people.FindByNameAsync(typed) is { } found) wanted = [.. wanted, found.Id];
                else unknown.Add(typed.Trim());
            }
            var current = PhotoTags.GetValueOrDefault(attachmentId)?.Select(p => p.Id).ToList() ?? [];
            foreach (var pid in current.Except(wanted)) await people.TagPhotoAsync(attachmentId, pid, false);
            foreach (var pid in wanted.Except(current)) await people.TagPhotoAsync(attachmentId, pid, true);
        }

        var refused = new List<string>();
        if (Request.HasFormContentType)
        {
            foreach (var file in Request.Form.Files.Where(f => f.Name == "Files" && f.Length > 0))
            {
                await using var stream = file.OpenReadStream();
                var (_, error) = await attachments.AddFromStreamAsync(Memory.Id, file.FileName, stream, User.UserId());
                if (error != UploadError.None) refused.Add($"{file.FileName} ({UploadMessages.Describe(error)})");
            }
        }
        var notes = new List<string>();
        if (unknown.Count > 0)
            notes.Add($"We couldn't find {string.Join(", ", unknown.Select(n => $"\"{n}\""))} in the family tree. You can add them under Family tree, then tag them here.");
        if (refused.Count > 0) notes.Add("These files couldn't be added: " + string.Join("; ", refused) + ".");
        return notes.Count == 0 ? null : "Your words are saved. " + string.Join(" ", notes);
    }

    private async Task<IActionResult> NextAsync()
    {
        var plan = await plans.GetAsync(Memory.StorytellerId);
        if (plan.Next is { } next && next.Prompt.Id != Memory.PromptId)
            return LocalRedirect($"/answer/{next.Prompt.Id}" + (IsProxy ? $"?for={Memory.StorytellerId}" : ""));
        return LocalRedirect(IsProxy ? $"/questions?for={Memory.StorytellerId}" : "/");
    }

    private string LocalTime() => TimeZoneInfo.ConvertTime(clock.GetUtcNow(), TimeZoneInfo.Local).ToString("h:mm tt");
}

public static class UploadMessages
{
    public static string Describe(UploadError error) => error switch
    {
        UploadError.TooLarge => "it's too big",
        UploadError.Unsupported => "this kind of file can't be added; photos, PDFs, sound and video files work",
        _ => "it didn't arrive completely; please try again",
    };
}
