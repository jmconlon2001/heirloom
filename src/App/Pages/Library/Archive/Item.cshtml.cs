using FamilyMemories.Archive;
using FamilyMemories.Data;
using FamilyMemories.Stories;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace FamilyMemories.Pages.Library.Archive;

public sealed class ItemModel(ArchiveService archive, LibraryService library) : PageModel
{
    public ArchiveItem Item { get; private set; } = null!;
    public bool CanEdit { get; private set; }
    public List<Memory> Stories { get; private set; } = [];
    public List<(Guid Id, string Label)> StoryChoices { get; private set; } = [];

    public async Task<IActionResult> OnGetAsync(Guid id)
    {
        var item = await archive.ItemAsync(id);
        if (item is null) return NotFound();
        Item = item;
        CanEdit = ArchiveService.CanEdit(User, item);
        // Linked stories, but only the ones this person may read.
        var visible = library.Visible(User).Select(m => m.Id);
        var linked = await archive.StoriesForItemAsync(id);
        var readable = await visible.Where(v => linked.Select(l => l.Id).Contains(v)).ToListAsync();
        Stories = [.. linked.Where(m => readable.Contains(m.Id))];
        if (CanEdit)
            StoryChoices = [.. (await library.Visible(User).Where(m => m.Body != "" || m.Attachments.Any()).Include(m => m.Storyteller).OrderBy(m => m.Title).Take(2000).ToListAsync())
                .Select(m => (m.Id, $"{m.Title} ({m.Storyteller!.DisplayName})"))];
        return Page();
    }

    public async Task<IActionResult> OnPostLinkAsync(Guid id, Guid memoryId)
    {
        var item = await archive.ItemAsync(id);
        if (item is null) return NotFound();
        if (!ArchiveService.CanEdit(User, item)) return Forbid();
        if (!await library.Visible(User).AnyAsync(m => m.Id == memoryId)) return NotFound();
        TempData["Flash"] = await archive.LinkStoryAsync(id, memoryId) ?? "Linked. The story now lists this under \"Related documents\".";
        return LocalRedirect($"/library/archive/item/{id}#stories-heading");
    }

    public async Task<IActionResult> OnPostUnlinkAsync(Guid id, Guid memoryId)
    {
        var item = await archive.ItemAsync(id);
        if (item is null) return NotFound();
        if (!ArchiveService.CanEdit(User, item)) return Forbid();
        await archive.UnlinkStoryAsync(id, memoryId);
        return LocalRedirect($"/library/archive/item/{id}#stories-heading");
    }
}
