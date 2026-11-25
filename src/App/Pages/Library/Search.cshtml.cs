using FamilyMemories.Data;
using FamilyMemories.Stories;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace FamilyMemories.Pages.Library;

public sealed class SearchModel(AppDbContext db, LibraryService library) : PageModel
{
    public string? Query { get; private set; }
    public List<StoryCard> Results { get; private set; } = [];
    public List<Person> People { get; private set; } = [];

    public async Task OnGetAsync(string? q)
    {
        if (string.IsNullOrWhiteSpace(q)) return;
        Query = q.Trim()[..Math.Min(q.Trim().Length, 200)];
        Results = await library.SearchAsync(User, Query);
        var like = $"%{Query.Replace("%", "").Replace("_", "")}%";
        People = await db.People.AsNoTracking()
            .Where(p => EF.Functions.ILike(p.GivenNames + " " + p.Surname, like) || EF.Functions.ILike(p.Nickname ?? "", like) || EF.Functions.ILike(p.BirthSurname ?? "", like))
            .OrderBy(p => p.GivenNames).Take(30).ToListAsync();
    }
}
