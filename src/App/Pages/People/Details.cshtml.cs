using FamilyMemories.Data;
using FamilyMemories.Family;
using FamilyMemories.Stories;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace FamilyMemories.Pages.People;

public sealed class DetailsModel(AppDbContext db, PeopleService people, LibraryService library, FamilyMemories.Web.ViewModeService views) : PageModel
{
    public bool Detailed { get; private set; }
    public Kin Kin { get; private set; } = null!;
    public List<StoryCard> ToldBy { get; private set; } = [];
    public List<StoryCard> About { get; private set; } = [];
    public List<Attachment> Photos { get; private set; } = [];
    public List<PersonFact> Facts { get; private set; } = [];
    public List<PersonLink> Links { get; private set; } = [];

    public async Task<IActionResult> OnGetAsync(Guid id)
    {
        var kin = await people.KinAsync(id);
        if (kin is null) return NotFound();
        Kin = kin;
        Detailed = await views.GetAsync(HttpContext) == FamilyMemories.Web.ViewMode.Detailed;
        Facts = await db.PersonFacts.AsNoTracking().Where(f => f.PersonId == id).OrderBy(f => f.SortOrder).ToListAsync();
        Links = await db.PersonLinks.AsNoTracking().Where(l => l.PersonId == id).OrderBy(l => l.SortOrder).ToListAsync();
        var visible = library.Visible(User);
        if (kin.Person.LinkedUserId is { } userId)
            ToldBy = await library.CardsAsync(visible.Where(m => m.StorytellerId == userId).OrderByDescending(m => m.CompletedAt));
        About = await library.CardsAsync(visible.Where(m => m.AboutPersonId == id || db.MemoryPeople.Any(x => x.MemoryId == m.Id && x.PersonId == id))
            .Where(m => kin.Person.LinkedUserId == null || m.StorytellerId != kin.Person.LinkedUserId).OrderByDescending(m => m.CompletedAt));
        var visibleIds = visible.Select(m => m.Id);
        Photos = await db.AttachmentPeople.AsNoTracking().Where(x => x.PersonId == id && x.Attachment!.MemoryId != null && visibleIds.Contains(x.Attachment.MemoryId.Value) && x.Attachment.RemovedAt == null)
            .Select(x => x.Attachment!).OrderByDescending(a => a.CreatedAt).Take(60).ToListAsync();
        return Page();
    }
}
