using FamilyMemories.Admin;
using FamilyMemories.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace FamilyMemories.Pages.Admin.Prompts;

public sealed class IndexModel(AppDbContext db, AuditLog audit) : PageModel
{
    public List<Chapter> Chapters { get; private set; } = [];
    /// <summary>Chapters the family added that have no questions at all (not even turned-off ones), so they can be removed.</summary>
    public HashSet<Guid> EmptyChapters { get; private set; } = [];

    public async Task OnGetAsync()
    {
        Chapters = await db.Chapters.AsNoTracking().OrderBy(c => c.SortOrder).ToListAsync();
        var prompts = await db.Prompts.AsNoTracking().Where(p => p.ForUserId == null && p.ChapterId != null)
            .OrderBy(p => p.SortOrder).ToListAsync();
        foreach (var c in Chapters) c.Prompts = [.. prompts.Where(p => p.ChapterId == c.Id)];
        var used = await db.Prompts.Where(p => p.ChapterId != null).Select(p => p.ChapterId!.Value).Distinct().ToListAsync();
        EmptyChapters = [.. Chapters.Where(c => c.Key is null).Select(c => c.Id).Except(used)];    // the built-in chapters stay
    }

    public async Task<IActionResult> OnPostRenameChapterAsync(Guid chapterId, string? title)
    {
        var chapter = await db.Chapters.SingleOrDefaultAsync(c => c.Id == chapterId);
        if (chapter is null) return NotFound();
        title = title?.Trim();
        if (string.IsNullOrEmpty(title) || title.Length > 120)
        {
            TempData["Flash"] = "Please give the chapter a name (up to 120 letters).";
            return LocalRedirect($"/admin/prompts#chapter-{chapter.Id}");
        }
        if (title != chapter.Title)
        {
            audit.Add("chapter.rename", $"Renamed chapter \"{chapter.Title}\" to \"{title}\"", "chapter", chapter.Id);
            chapter.Title = title;
            chapter.EditedByAdmin = true;
            await db.SaveChangesAsync();
            TempData["Flash"] = $"The chapter is now called \"{title}\".";
        }
        return LocalRedirect($"/admin/prompts#chapter-{chapter.Id}");
    }

    public async Task<IActionResult> OnPostMoveChapterAsync(Guid chapterId, int direction)
    {
        var chapters = await db.Chapters.OrderBy(c => c.SortOrder).ToListAsync();
        var i = chapters.FindIndex(c => c.Id == chapterId);
        if (i < 0) return NotFound();
        var j = i + Math.Sign(direction);
        if (j >= 0 && j < chapters.Count)
        {
            (chapters[i], chapters[j]) = (chapters[j], chapters[i]);
            for (var k = 0; k < chapters.Count; k++)
            {
                chapters[k].SortOrder = (k + 1) * 10;
                chapters[k].EditedByAdmin = true;                    // keep the family's order from now on
            }
            audit.Add("chapter.move", $"Moved chapter \"{chapters[j].Title}\" {(direction < 0 ? "up" : "down")}", "chapter", chapterId);
            await db.SaveChangesAsync();
        }
        return LocalRedirect($"/admin/prompts#chapter-{chapterId}");
    }

    public async Task<IActionResult> OnPostAddChapterAsync(string? title)
    {
        title = title?.Trim();
        if (string.IsNullOrEmpty(title) || title.Length > 120)
        {
            TempData["Flash"] = "Please give the new chapter a name (up to 120 letters).";
            return LocalRedirect("/admin/prompts#add-chapter");
        }
        var last = await db.Chapters.MaxAsync(c => (int?)c.SortOrder) ?? 0;
        var chapter = new Chapter { Id = Guid.CreateVersion7(), Title = title, SortOrder = last + 10, EditedByAdmin = true };
        db.Chapters.Add(chapter);
        audit.Add("chapter.add", $"Added chapter \"{title}\"", "chapter", chapter.Id);
        await db.SaveChangesAsync();
        TempData["Flash"] = $"Added the chapter \"{title}\". Use \"Add a question\" to put questions in it.";
        return LocalRedirect($"/admin/prompts#chapter-{chapter.Id}");
    }

    public async Task<IActionResult> OnPostDeleteChapterAsync(Guid chapterId)
    {
        var chapter = await db.Chapters.SingleOrDefaultAsync(c => c.Id == chapterId);
        if (chapter is null) return NotFound();
        if (chapter.Key is not null || await db.Prompts.AnyAsync(p => p.ChapterId == chapterId))
        {
            TempData["Flash"] = "That chapter still has questions, so it wasn't removed.";
            return LocalRedirect($"/admin/prompts#chapter-{chapterId}");
        }
        db.Chapters.Remove(chapter);
        audit.Add("chapter.remove", $"Removed empty chapter \"{chapter.Title}\"", "chapter", chapter.Id);
        await db.SaveChangesAsync();
        TempData["Flash"] = $"Removed the empty chapter \"{chapter.Title}\".";
        return LocalRedirect("/admin/prompts");
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
