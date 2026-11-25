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

    public string? WriterName { get; private set; }
    public int CurrentYear => clock.GetUtcNow().Year;

    private async Task<bool> LoadAsync(Guid id)
    {
        var memory = await db.Memories
            .Include(m => m.Prompt).ThenInclude(p => p!.Chapter)
            .Include(m => m.Prompt).ThenInclude(p => p!.AskedByUser)
            .Include(m => m.Storyteller)
            .Include(m => m.WrittenBy)
            .AsNoTracking()
            .SingleOrDefaultAsync(m => m.Id == id);
        if (memory is null || !await access.CanEditAsync(User, memory)) return false;

        Memory = memory;
        Items = await db.Attachments.AsNoTracking()
            .Where(a => a.MemoryId == id && a.RemovedAt == null)
            .OrderBy(a => a.SortOrder).ThenBy(a => a.CreatedAt).ToListAsync();
        WriterName = memory.WrittenBy?.DisplayName ?? (IsProxy ? User.DisplayName() : null);
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
        return refused.Count == 0 ? null : "Your words are saved. These files couldn't be added: " + string.Join("; ", refused) + ".";
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
