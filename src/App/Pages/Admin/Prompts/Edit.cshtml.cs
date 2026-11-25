using FamilyMemories.Admin;
using FamilyMemories.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace FamilyMemories.Pages.Admin.Prompts;

public sealed class EditModel(AppDbContext db, AuditLog audit, TimeProvider clock) : PageModel
{
    public bool IsNew { get; private set; }
    public List<Chapter> Chapters { get; private set; } = [];
    public string? Error { get; private set; }

    [BindProperty] public Guid ChapterId { get; set; }
    [BindProperty] public string? Question { get; set; }
    [BindProperty] public string? Title { get; set; }
    [BindProperty] public string? Hints { get; set; }
    [BindProperty] public string? SuggestedAttachments { get; set; }
    [BindProperty] public bool IsCore { get; set; }

    public async Task<IActionResult> OnGetAsync(string id)
    {
        Chapters = await db.Chapters.AsNoTracking().OrderBy(c => c.SortOrder).ToListAsync();
        if (id == "new")
        {
            IsNew = true;
            ChapterId = Chapters.FirstOrDefault()?.Id ?? Guid.Empty;
            return Page();
        }
        if (!Guid.TryParse(id, out var promptId)) return NotFound();
        var p = await db.Prompts.AsNoTracking().SingleOrDefaultAsync(x => x.Id == promptId && x.ForUserId == null);
        if (p is null) return NotFound();
        (ChapterId, Question, Title, Hints, SuggestedAttachments, IsCore) =
            (p.ChapterId ?? Guid.Empty, p.Question, p.Title, string.Join('\n', p.Hints), p.SuggestedAttachments, p.IsCore);
        return Page();
    }

    public async Task<IActionResult> OnPostAsync(string id)
    {
        IsNew = id == "new";
        Chapters = await db.Chapters.AsNoTracking().OrderBy(c => c.SortOrder).ToListAsync();
        if (string.IsNullOrWhiteSpace(Question)) { Error = "Please type the question."; return Page(); }
        if (Chapters.All(c => c.Id != ChapterId)) { Error = "Please choose a chapter."; return Page(); }

        Prompt? prompt;
        if (IsNew)
        {
            var last = await db.Prompts.Where(p => p.ChapterId == ChapterId).MaxAsync(p => (int?)p.SortOrder) ?? 0;
            prompt = new Prompt { Id = Guid.CreateVersion7(), CreatedAt = clock.GetUtcNow().UtcDateTime, SortOrder = last + 10, IsActive = true };
            db.Prompts.Add(prompt);
        }
        else
        {
            prompt = Guid.TryParse(id, out var promptId)
                ? await db.Prompts.SingleOrDefaultAsync(x => x.Id == promptId && x.ForUserId == null) : null;
            if (prompt is null) return NotFound();
        }

        var question = Question.Trim();
        prompt.ChapterId = ChapterId;
        prompt.Question = question;
        prompt.Title = string.IsNullOrWhiteSpace(Title) ? (question.Length > 80 ? question[..79] + "…" : question) : Title.Trim();
        prompt.Hints = [.. (Hints ?? "").Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Take(6)];
        prompt.SuggestedAttachments = string.IsNullOrWhiteSpace(SuggestedAttachments) ? null : SuggestedAttachments.Trim();
        prompt.IsCore = IsCore;
        prompt.EditedByAdmin = true;
        audit.Add(IsNew ? "prompt.add" : "prompt.edit", $"{(IsNew ? "Added" : "Edited")} question \"{prompt.Title}\"", "prompt", prompt.Id);
        await db.SaveChangesAsync();

        TempData["Flash"] = "Question saved.";
        return LocalRedirect($"/admin/prompts#chapter-{ChapterId}");
    }
}
