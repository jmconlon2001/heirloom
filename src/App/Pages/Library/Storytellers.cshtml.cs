using FamilyMemories.Data;
using FamilyMemories.Stories;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace FamilyMemories.Pages.Library;

public sealed class StorytellersModel(AppDbContext db, LibraryService library) : PageModel
{
    public sealed record TellerRow(Guid Id, string Name, int Count, Guid? PhotoId);
    public List<TellerRow> Tellers { get; private set; } = [];
    public AppUser? Teller { get; private set; }
    public Guid? PersonId { get; private set; }
    public List<StoryCard> Cards { get; private set; } = [];

    public async Task<IActionResult> OnGetAsync(Guid? id)
    {
        var visible = library.Visible(User);
        if (id is null)
        {
            var counts = await visible.GroupBy(m => new { m.StorytellerId, m.Storyteller!.DisplayName })
                .Select(g => new { g.Key.StorytellerId, g.Key.DisplayName, Count = g.Count() }).ToListAsync();
            var photos = await db.People.Where(p => p.LinkedUserId != null && p.PhotoAttachmentId != null)
                .ToDictionaryAsync(p => p.LinkedUserId!.Value, p => p.PhotoAttachmentId!.Value);
            Tellers = [.. counts.OrderBy(c => c.DisplayName).Select(c => new TellerRow(c.StorytellerId, c.DisplayName, c.Count, photos.TryGetValue(c.StorytellerId, out var ph) ? ph : null))];
            return Page();
        }
        Teller = await db.Users.AsNoTracking().SingleOrDefaultAsync(u => u.Id == id);
        if (Teller is null) return NotFound();
        PersonId = await db.People.Where(p => p.LinkedUserId == id).Select(p => (Guid?)p.Id).FirstOrDefaultAsync();
        Cards = await library.CardsAsync(visible.Where(m => m.StorytellerId == id).OrderBy(m => m.Prompt!.Chapter!.SortOrder).ThenBy(m => m.Prompt!.SortOrder));
        return Page();
    }
}
