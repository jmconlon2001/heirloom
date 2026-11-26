using FamilyMemories.Archive;
using FamilyMemories.Auth;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace FamilyMemories.Pages.Library.Archive;

/// <summary>The family archive: folders and the items in them, or everything carrying a keyword (D-080).</summary>
public sealed class IndexModel(ArchiveService archive) : PageModel
{
    public ArchiveFolderView View { get; private set; } = null!;
    public IReadOnlyList<KeywordNode> Keywords { get; private set; } = [];
    public string? Keyword { get; private set; }

    public async Task<IActionResult> OnGetAsync(Guid? folder, string? k)
    {
        Keyword = string.IsNullOrWhiteSpace(k) ? null : k.Trim();
        var view = await archive.FolderAsync(Keyword is null ? folder : null, Keyword);
        if (view is null) return NotFound();
        View = view;
        Keywords = await archive.KeywordTreeAsync();
        return Page();
    }

    public async Task<IActionResult> OnPostNewFolderAsync(Guid? folder, string? title, string? description)
    {
        if (!User.IsAdmin()) return Forbid();
        if (string.IsNullOrWhiteSpace(title)) { TempData["Flash"] = "Please give the folder a name."; return LocalRedirect(Here(folder)); }
        var created = await archive.CreateFolderAsync(folder, title, description);
        return LocalRedirect(Here(created.Id));
    }

    public async Task<IActionResult> OnPostRenameFolderAsync(Guid folder, string? title, string? description)
    {
        if (!User.IsAdmin()) return Forbid();
        if (!string.IsNullOrWhiteSpace(title)) await archive.RenameFolderAsync(folder, title, description);
        return LocalRedirect(Here(folder));
    }

    public async Task<IActionResult> OnPostDeleteFolderAsync(Guid folder, Guid? parent)
    {
        if (!User.IsAdmin()) return Forbid();
        var error = await archive.DeleteFolderAsync(folder);
        TempData["Flash"] = error ?? "Folder removed.";
        return LocalRedirect(error is null ? Here(parent) : Here(folder));
    }

    private static string Here(Guid? folder) => folder is { } f ? $"/library/archive/{f}" : "/library/archive";
}
