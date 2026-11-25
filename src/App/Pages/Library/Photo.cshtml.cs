using FamilyMemories.Data;
using FamilyMemories.Stories;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace FamilyMemories.Pages.Library;

/// <summary>The large photo viewer: one photo, big, with plain Previous / Next buttons (no swiping needed).</summary>
public sealed class PhotoModel(AppDbContext db, LibraryService library) : PageModel
{
    public Attachment Photo { get; private set; } = null!;
    public List<Person> People { get; private set; } = [];
    public Guid? PreviousId { get; private set; }
    public Guid? NextId { get; private set; }

    public async Task<IActionResult> OnGetAsync(Guid id)
    {
        var ids = await library.Photos(User).Select(a => a.Id).Take(2000).ToListAsync();
        var index = ids.IndexOf(id);
        if (index < 0) return NotFound();
        Photo = await db.Attachments.AsNoTracking().Include(a => a.Memory).SingleAsync(a => a.Id == id);
        People = await db.AttachmentPeople.AsNoTracking().Where(x => x.AttachmentId == id).Select(x => x.Person!).ToListAsync();
        PreviousId = index > 0 ? ids[index - 1] : null;
        NextId = index < ids.Count - 1 ? ids[index + 1] : null;
        return Page();
    }
}
