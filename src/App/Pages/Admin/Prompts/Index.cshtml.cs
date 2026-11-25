using FamilyMemories.Admin;
using FamilyMemories.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace FamilyMemories.Pages.Admin.Prompts;

public sealed class IndexModel(AppDbContext db, AuditLog audit) : PageModel
{
    public List<Chapter> Chapters { get; private set; } = [];

    public async Task OnGetAsync()
    {
        Chapters = await db.Chapters.AsNoTracking().OrderBy(c => c.SortOrder).ToListAsync();
        var prompts = await db.Prompts.AsNoTracking().Where(p => p.ForUserId == null && p.ChapterId != null)
            .OrderBy(p => p.SortOrder).ToListAsync();
        foreach (var c in Chapters) c.Prompts = [.. prompts.Where(p => p.ChapterId == c.Id)];
    }

    public async Task<IActionResult> OnPostMoveAsync(Guid promptId, int direction)
    {
        var prompt = await db.Prompts.SingleOrDefaultAsync(p => p.Id == promptId);
        if (prompt?.ChapterId is null) return NotFound();
        var siblings = await db.Prompts.Where(p => p.ChapterId == prompt.ChapterId && p.ForUserId == null)
            .OrderBy(p => p.SortOrder).ToListAsync();
        var i = siblings.FindIndex(p => p.Id == promptId);
        var j = i + Math.Sign(direction);
        if (j >= 0 && j < siblings.Count)
        {
            (siblings[i], siblings[j]) = (siblings[j], siblings[i]);
            for (var k = 0; k < siblings.Count; k++) siblings[k].SortOrder = (k + 1) * 10;
            prompt.EditedByAdmin = true;
            audit.Add("prompt.move", $"Moved \"{prompt.Title}\" {(direction < 0 ? "up" : "down")}", "prompt", prompt.Id);
            await db.SaveChangesAsync();
        }
        return LocalRedirect($"/admin/prompts#chapter-{prompt.ChapterId}");
    }

    public async Task<IActionResult> OnPostToggleAsync(Guid promptId)
    {
        var prompt = await db.Prompts.SingleOrDefaultAsync(p => p.Id == promptId);
        if (prompt is null) return NotFound();
        prompt.IsActive = !prompt.IsActive;
        audit.Add(prompt.IsActive ? "prompt.on" : "prompt.off", $"Turned {(prompt.IsActive ? "on" : "off")} \"{prompt.Title}\"", "prompt", prompt.Id);
        await db.SaveChangesAsync();
        TempData["Flash"] = $"\"{prompt.Title}\" is now turned {(prompt.IsActive ? "on" : "off")}.";
        return LocalRedirect($"/admin/prompts#chapter-{prompt.ChapterId}");
    }
}
