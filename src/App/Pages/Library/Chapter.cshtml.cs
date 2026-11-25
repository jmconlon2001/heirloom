using FamilyMemories.Data;
using FamilyMemories.Stories;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace FamilyMemories.Pages.Library;

public sealed class ChapterModel(AppDbContext db, LibraryService library) : PageModel
{
    public string Title { get; private set; } = "";
    public List<StoryCard> Cards { get; private set; } = [];

    public async Task<IActionResult> OnGetAsync(Guid id)
    {
        var chapter = await db.Chapters.AsNoTracking().SingleOrDefaultAsync(c => c.Id == id);
        if (chapter is null) return NotFound();
        Title = chapter.Title;
        Cards = await library.CardsAsync(library.Visible(User).Where(m => m.Prompt != null && m.Prompt.ChapterId == id)
            .OrderBy(m => m.Prompt!.SortOrder).ThenBy(m => m.Storyteller!.DisplayName));
        return Page();
    }
}
