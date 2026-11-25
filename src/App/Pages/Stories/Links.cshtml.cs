using FamilyMemories.Auth;
using FamilyMemories.Data;
using FamilyMemories.Family;
using FamilyMemories.Stories;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace FamilyMemories.Pages.Stories;

[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed class LinksModel(AppDbContext db, StoryAccess access, SharingService sharing) : PageModel
{
    public Memory Memory { get; private set; } = null!;
    public List<LinkShare> Links { get; private set; } = [];
    public string? NewLink { get; private set; }
    public string? NewLinkQr { get; private set; }
    public bool NewLinkHasPasscode { get; private set; }

    private async Task<bool> LoadAsync(Guid id)
    {
        var memory = await db.Memories.AsNoTracking().SingleOrDefaultAsync(m => m.Id == id);
        if (memory is null || !await access.CanEditAsync(User, memory)) return false;
        Memory = memory;
        Links = await db.LinkShares.AsNoTracking().Where(l => l.MemoryId == id).OrderByDescending(l => l.CreatedAt).ToListAsync();
        return true;
    }

    public async Task<IActionResult> OnGetAsync(Guid id) => await LoadAsync(id) ? Page() : NotFound();

    public async Task<IActionResult> OnPostCreateAsync(Guid id, string? label, int expires, string? passcode)
    {
        if (!await LoadAsync(id)) return NotFound();
        TimeSpan? lifetime = expires > 0 ? TimeSpan.FromDays(Math.Min(expires, 3650)) : null;
        var token = await sharing.CreateLinkAsync(id, User.UserId(), lifetime, passcode, label);
        await LoadAsync(id);
        NewLink = sharing.LinkUrl(token).ToString();
        NewLinkQr = FamilyMemories.Pages.Admin.Users.LoginCardModel.QrPng(NewLink);
        NewLinkHasPasscode = !string.IsNullOrWhiteSpace(passcode);
        return Page();
    }

    public async Task<IActionResult> OnPostRevokeAsync(Guid id, Guid shareId)
    {
        if (!await LoadAsync(id)) return NotFound();
        await sharing.RevokeAsync(id, shareId);
        TempData["Flash"] = "That link has been turned off.";
        return LocalRedirect($"/stories/{id}/links");
    }
}
