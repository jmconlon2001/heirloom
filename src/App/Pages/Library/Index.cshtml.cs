using FamilyMemories.Data;
using FamilyMemories.Stories;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace FamilyMemories.Pages.Library;

public sealed class IndexModel(AppDbContext db, LibraryService library) : PageModel
{
    public sealed record ChapterCount(Guid Id, string Title, int Count);
    public List<StoryCard> Newest { get; private set; } = [];
    public List<ChapterCount> Chapters { get; private set; } = [];

    public async Task OnGetAsync()
    {
        Newest = await library.NewestAsync(User, 12);
        var visible = library.Visible(User);
        var counts = await db.Chapters.AsNoTracking().OrderBy(c => c.SortOrder)
            .Select(c => new { c.Id, c.Title, Count = visible.Count(m => m.Prompt != null && m.Prompt.ChapterId == c.Id) })
            .ToListAsync();
        Chapters = [.. counts.Where(c => c.Count > 0).Select(c => new ChapterCount(c.Id, c.Title, c.Count))];
    }
}
