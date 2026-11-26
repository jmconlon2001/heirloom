using FamilyMemories.Archive;
using FamilyMemories.Auth;
using FamilyMemories.Data;
using FamilyMemories.Imports;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace FamilyMemories.Pages.Library.Archive;

/// <summary>Add to the family archive, or change an item: its words, its folder, its pages (D-080).</summary>
[DisableRequestSizeLimit]
[RequestFormLimits(MultipartBodyLengthLimit = long.MaxValue)]
public sealed class EditModel(ArchiveService archive, TimeProvider clock) : PageModel
{
    public ArchiveItem? Item { get; private set; }
    public List<(Guid Id, string Name)> Folders { get; private set; } = [];
    public string? Error { get; private set; }

    [BindProperty] public string? Title { get; set; }
    [BindProperty] public string? Description { get; set; }
    [BindProperty] public string? When { get; set; }
    [BindProperty] public string? Place { get; set; }
    [BindProperty] public string? Transcription { get; set; }
    [BindProperty] public string? Provenance { get; set; }
    [BindProperty] public Guid? Folder { get; set; }
    [BindProperty] public bool EachFileSeparately { get; set; }
    [BindProperty] public List<IFormFile> Files { get; set; } = [];

    private async Task<bool> LoadAsync(Guid? id)
    {
        Folders = await archive.FolderChoicesAsync();
        if (id is null) return true;
        Item = await archive.ItemAsync(id.Value);
        return Item is not null && ArchiveService.CanEdit(User, Item);
    }

    public async Task<IActionResult> OnGetAsync(Guid? id, Guid? folder)
    {
        if (!await LoadAsync(id)) return id is null ? Forbid() : NotFound();
        if (Item is { } i)
            (Title, Description, When, Place, Transcription, Provenance, Folder) =
                (i.Title, i.Description, i.When.Format(), i.Place, i.Transcription, i.Provenance, i.CollectionId);
        else Folder = folder;
        return Page();
    }

    public async Task<IActionResult> OnPostSaveAsync(Guid? id)
    {
        if (!await LoadAsync(id)) return NotFound();
        var files = Files.Where(f => f.Length > 0).ToList();
        var bulk = Item is null && EachFileSeparately && files.Count > 0;
        if (!bulk && string.IsNullOrWhiteSpace(Title)) { Error = "Please give it a title, like \"Letter from Joe, 1944\"."; return Page(); }
        if (Folder is { } f && Folders.All(x => x.Id != f)) Folder = null;
        var (date, _) = DateText.Parse(When, clock.GetUtcNow().Year);   // words that aren't a date are kept as words
        var input = new ArchiveItemInput(Title ?? "", Description, date, Place, Transcription, Provenance, Folder);

        var streams = files.Select(x => (x.FileName, (Stream)x.OpenReadStream())).ToList();
        try
        {
            if (Item is null)
            {
                var (created, problems) = await archive.CreateAsync(input, streams, bulk, User.UserId());
                TempData["Flash"] = problems.Count > 0 ? string.Join(" ", problems)
                    : bulk ? $"Added {created.Count} items to the archive." : null;
                return LocalRedirect(bulk || created.Count != 1 ? (Folder is { } folder ? $"/library/archive/{folder}" : "/library/archive") : $"/library/archive/item/{created[0]}");
            }
            await archive.UpdateAsync(Item.Id, input);
            var issues = new List<string>();
            foreach (var (name, content) in streams)
                if (await archive.AddFileAsync(Item.Id, name, content, User.UserId()) is { } problem) issues.Add(problem);
            TempData["Flash"] = issues.Count > 0 ? string.Join(" ", issues) : "Saved.";
            return LocalRedirect($"/library/archive/item/{Item.Id}");
        }
        finally
        {
            foreach (var (_, s) in streams) await s.DisposeAsync();
        }
    }

    public async Task<IActionResult> OnPostRemoveFileAsync(Guid id, Guid attachmentId)
    {
        if (!await LoadAsync(id)) return NotFound();
        await archive.RemoveFileAsync(id, attachmentId);
        TempData["Flash"] = "Removed that page.";
        return LocalRedirect($"/library/archive/edit/{id}");
    }

    public async Task<IActionResult> OnPostDeleteAsync(Guid id, bool confirm)
    {
        if (!await LoadAsync(id)) return NotFound();
        if (!confirm) { Error = "Tick the box to confirm, then press Remove again."; return Page(); }
        var folder = Item!.CollectionId;
        await archive.DeleteAsync(id);
        TempData["Flash"] = $"\"{Item.Title}\" was removed from the archive.";
        return LocalRedirect(folder is { } f ? $"/library/archive/{f}" : "/library/archive");
    }
}
