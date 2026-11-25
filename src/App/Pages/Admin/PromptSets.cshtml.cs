using FamilyMemories.Admin;
using FamilyMemories.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace FamilyMemories.Pages.Admin;

public sealed class PromptSetsModel(AppDbContext db, AuditLog audit, TimeProvider clock) : PageModel
{
    public List<PromptSet> Sets { get; private set; } = [];
    public List<Chapter> Chapters { get; private set; } = [];
    public PromptSet? Editing { get; private set; }
    public HashSet<Guid> Selected { get; private set; } = [];

    public async Task<IActionResult> OnGetAsync(Guid? id)
    {
        await LoadAsync();
        if (id is not null)
        {
            Editing = Sets.SingleOrDefault(s => s.Id == id && s.Kind == PromptSetKind.Custom);
            if (Editing is null) return NotFound();
            Selected = [.. Editing.Items.Select(i => i.PromptId)];
        }
        return Page();
    }

    public async Task<IActionResult> OnPostAsync(Guid? id, string? name, List<Guid> promptIds)
    {
        PromptSet? set;
        if (id is null)
        {
            set = new PromptSet { Id = Guid.CreateVersion7(), Kind = PromptSetKind.Custom, CreatedAt = clock.GetUtcNow().UtcDateTime };
            db.PromptSets.Add(set);
        }
        else
        {
            set = await db.PromptSets.Include(s => s.Items).SingleOrDefaultAsync(s => s.Id == id && s.Kind == PromptSetKind.Custom);
            if (set is null) return NotFound();
            set.Items.Clear();
        }
        set.Name = string.IsNullOrWhiteSpace(name) ? "My question set" : name.Trim();
        var valid = await db.Prompts.Where(p => promptIds.Contains(p.Id) && p.ForUserId == null).Select(p => p.Id).ToListAsync();
        foreach (var pid in valid) set.Items.Add(new PromptSetItem { PromptSetId = set.Id, PromptId = pid });
        audit.Add("promptset.save", $"Saved question set \"{set.Name}\" with {valid.Count} questions", "promptset", set.Id);
        await db.SaveChangesAsync();
        TempData["Flash"] = $"\"{set.Name}\" saved with {valid.Count} questions.";
        return LocalRedirect("/admin/prompt-sets");
    }

    private async Task LoadAsync()
    {
        Sets = await db.PromptSets.AsNoTracking().Include(s => s.Items).OrderBy(s => s.Kind).ThenBy(s => s.Name).ToListAsync();
        Chapters = await db.Chapters.AsNoTracking().OrderBy(c => c.SortOrder).ToListAsync();
        var prompts = await db.Prompts.AsNoTracking().Where(p => p.ForUserId == null && p.IsActive && p.ChapterId != null)
            .OrderBy(p => p.SortOrder).ToListAsync();
        foreach (var c in Chapters) c.Prompts = [.. prompts.Where(p => p.ChapterId == c.Id)];
    }
}
