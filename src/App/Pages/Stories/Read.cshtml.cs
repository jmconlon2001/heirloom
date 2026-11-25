using FamilyMemories.Auth;
using FamilyMemories.Data;
using FamilyMemories.Family;
using FamilyMemories.Stories;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace FamilyMemories.Pages.Stories;

public sealed class ReadModel(AppDbContext db, StoryAccess access, ConversationService conversation, TimeProvider clock) : PageModel
{
    public Memory Memory { get; private set; } = null!;
    public List<Attachment> Items { get; private set; } = [];
    public bool CanEdit { get; private set; }
    public Guid Me { get; private set; }
    /// <summary>Blank lines separate paragraphs; single line breaks are kept.</summary>
    public List<string[]> Paragraphs { get; private set; } = [];
    public List<Person> People { get; private set; } = [];
    public Dictionary<Guid, List<Person>> PhotoPeople { get; private set; } = [];
    public List<string> HeartNames { get; private set; } = [];
    public bool IHearted { get; private set; }
    public List<MemoryComment> Comments { get; private set; } = [];
    public string VisibilityText { get; private set; } = "";

    public string HeartSummary => HeartNames.Count switch
    {
        1 => $"{HeartNames[0]} loves this.",
        2 => $"{HeartNames[0]} and {HeartNames[1]} love this.",
        _ => $"{HeartNames[0]}, {HeartNames[1]} and {HeartNames.Count - 2} more love this.",
    };

    private async Task<bool> LoadAsync(Guid id)
    {
        var memory = await access.Readable(User).AsNoTracking()
            .Include(m => m.Prompt).Include(m => m.Storyteller).Include(m => m.WrittenBy)
            .SingleOrDefaultAsync(m => m.Id == id);
        if (memory is null) return false;

        Memory = memory;
        Me = User.UserId();
        CanEdit = await access.CanEditAsync(User, memory);
        Items = await db.Attachments.AsNoTracking().Where(a => a.MemoryId == id && a.RemovedAt == null)
            .OrderBy(a => a.SortOrder).ThenBy(a => a.CreatedAt).ToListAsync();
        Paragraphs = [.. memory.Body.Split("\n\n", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(p => p.Split('\n'))];
        People = await db.MemoryPeople.AsNoTracking().Where(x => x.MemoryId == id).Select(x => x.Person!).OrderBy(p => p.GivenNames).ToListAsync();
        var itemIds = Items.Select(a => a.Id).ToList();
        PhotoPeople = (await db.AttachmentPeople.AsNoTracking().Include(x => x.Person).Where(x => itemIds.Contains(x.AttachmentId)).ToListAsync())
            .GroupBy(x => x.AttachmentId).ToDictionary(g => g.Key, g => g.Select(x => x.Person!).ToList());
        var hearts = await db.Reactions.AsNoTracking().Include(r => r.User).Where(r => r.MemoryId == id).OrderBy(r => r.CreatedAt).ToListAsync();
        IHearted = hearts.Any(h => h.UserId == Me);
        HeartNames = [.. hearts.Select(h => h.UserId == Me ? "You" : h.User!.DisplayName)];
        Comments = await db.Comments.AsNoTracking().Include(c => c.User).Where(c => c.MemoryId == id && c.DeletedAt == null)
            .OrderBy(c => c.CreatedAt).ToListAsync();
        VisibilityText = memory.Visibility switch
        {
            Visibility.AllFamily => "All the family can read this story.",
            Visibility.SelectedPeople => "Shared with: " + string.Join(", ", await db.MemoryShares.Where(s => s.MemoryId == id).Select(s => s.User!.DisplayName).ToListAsync()) + ".",
            Visibility.Private => "Only you can read this story (and family admins).",
            _ => "Only people with a special link can read this story (and family admins).",
        };
        return true;
    }

    public async Task<IActionResult> OnGetAsync(Guid id)
    {
        if (!await LoadAsync(id)) return NotFound();
        await access.RecordViewAsync(User, Memory, clock);
        return Page();
    }

    public async Task<IActionResult> OnPostHeartAsync(Guid id)
    {
        if (!await LoadAsync(id) || Memory.Status != MemoryStatus.Complete) return NotFound();
        await conversation.ToggleHeartAsync(id, Me);
        return LocalRedirect($"/stories/{id}#conversation-heading");
    }

    public async Task<IActionResult> OnPostCommentAsync(Guid id, string? newComment)
    {
        if (!await LoadAsync(id) || Memory.Status != MemoryStatus.Complete) return NotFound();
        if (await conversation.AddCommentAsync(id, Me, newComment) is null) TempData["Flash"] = "Please write something first.";
        return LocalRedirect($"/stories/{id}#conversation-heading");
    }

    public async Task<IActionResult> OnPostDeleteCommentAsync(Guid id, Guid commentId)
    {
        if (!await LoadAsync(id)) return NotFound();
        await conversation.DeleteCommentAsync(commentId, Me, User.IsAdmin());
        return LocalRedirect($"/stories/{id}#conversation-heading");
    }

    public async Task<IActionResult> OnPostFollowUpAsync(Guid id, string? question)
    {
        if (!await LoadAsync(id) || Memory.Status != MemoryStatus.Complete) return NotFound();
        var prompt = await conversation.AskFollowUpAsync(Memory, Me, question);
        TempData["Flash"] = prompt is null ? "Please type your question first." : $"Thank you. {Memory.Storyteller!.DisplayName.Split(' ')[0]} will see your question next time.";
        return LocalRedirect($"/stories/{id}");
    }
}
