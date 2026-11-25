using FamilyMemories.Data;
using FamilyMemories.Family;
using FamilyMemories.Stories;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace FamilyMemories.Pages.People;

public sealed class DetailsModel(AppDbContext db, PeopleService people, LibraryService library) : PageModel
{
    public Kin Kin { get; private set; } = null!;
    public List<StoryCard> ToldBy { get; private set; } = [];
    public List<StoryCard> About { get; private set; } = [];
    public List<Attachment> Photos { get; private set; } = [];

    public async Task<IActionResult> OnGetAsync(Guid id)
    {
        var kin = await people.KinAsync(id);
        if (kin is null) return NotFound();
        Kin = kin;
        var visible = library.Visible(User);
        if (kin.Person.LinkedUserId is { } userId)
            ToldBy = await library.CardsAsync(visible.Where(m => m.StorytellerId == userId).OrderByDescending(m => m.CompletedAt));
        About = await library.CardsAsync(visible.Where(m => m.AboutPersonId == id || db.MemoryPeople.Any(x => x.MemoryId == m.Id && x.PersonId == id))
            .Where(m => kin.Person.LinkedUserId == null || m.StorytellerId != kin.Person.LinkedUserId).OrderByDescending(m => m.CompletedAt));
        var visibleIds = visible.Select(m => m.Id);
        Photos = await db.AttachmentPeople.AsNoTracking().Where(x => x.PersonId == id && visibleIds.Contains(x.Attachment!.MemoryId) && x.Attachment.RemovedAt == null)
            .Select(x => x.Attachment!).OrderByDescending(a => a.CreatedAt).Take(60).ToListAsync();
        return Page();
    }
}
