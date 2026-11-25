using FamilyMemories.Auth;
using FamilyMemories.Data;
using FamilyMemories.Stories;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace FamilyMemories.Pages.Admin.Stories;

public sealed class HistoryModel(AppDbContext db, MemoryService memories) : PageModel
{
    public Memory Memory { get; private set; } = null!;
    public List<MemoryVersion> Versions { get; private set; } = [];

    public async Task<IActionResult> OnGetAsync(Guid id)
    {
        var memory = await db.Memories.AsNoTracking().Include(m => m.Storyteller).SingleOrDefaultAsync(m => m.Id == id);
        if (memory is null) return NotFound();
        Memory = memory;
        Versions = await db.MemoryVersions.AsNoTracking().Include(v => v.SavedBy)
            .Where(v => v.MemoryId == id).OrderByDescending(v => v.Number).ToListAsync();
        return Page();
    }

    public async Task<IActionResult> OnPostAsync(Guid id, int number)
    {
        if (!await memories.RestoreVersionAsync(id, number, User.UserId())) return NotFound();
        TempData["Flash"] = $"Version {number} has been restored.";
        return LocalRedirect($"/admin/stories/{id}/history");
    }
}
