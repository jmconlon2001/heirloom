using FamilyMemories.Data;
using FamilyMemories.Stories;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace FamilyMemories.Pages.Stories;

public sealed class ReadModel(AppDbContext db, StoryAccess access) : PageModel
{
    public Memory Memory { get; private set; } = null!;
    public List<Attachment> Items { get; private set; } = [];
    public bool CanEdit { get; private set; }
    /// <summary>Blank lines separate paragraphs; single line breaks are kept.</summary>
    public List<string[]> Paragraphs { get; private set; } = [];

    public async Task<IActionResult> OnGetAsync(Guid id)
    {
        var memory = await db.Memories.AsNoTracking()
            .Include(m => m.Prompt).Include(m => m.Storyteller).Include(m => m.WrittenBy)
            .SingleOrDefaultAsync(m => m.Id == id);
        if (memory is null || !await access.CanViewAsync(User, memory)) return NotFound();

        Memory = memory;
        CanEdit = await access.CanEditAsync(User, memory);
        Items = await db.Attachments.AsNoTracking().Where(a => a.MemoryId == id && a.RemovedAt == null)
            .OrderBy(a => a.SortOrder).ThenBy(a => a.CreatedAt).ToListAsync();
        Paragraphs = [.. memory.Body.Split("\n\n", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(p => p.Split('\n'))];
        return Page();
    }
}
