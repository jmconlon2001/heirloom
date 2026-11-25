using FamilyMemories.Data;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace FamilyMemories.Pages.People;

public sealed class IndexModel(AppDbContext db) : PageModel
{
    public List<Person> People { get; private set; } = [];
    public async Task OnGetAsync() =>
        People = await db.People.AsNoTracking().OrderBy(p => p.Surname).ThenBy(p => p.GivenNames).ToListAsync();
}
