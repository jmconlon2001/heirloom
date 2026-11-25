using FamilyMemories.Data;
using FamilyMemories.Family;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace FamilyMemories.Pages.Share;

/// <summary>A story opened through a special link: read-only, no account needed.</summary>
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed class ViewModel(AppDbContext db, SharingService sharing) : PageModel
{
    public string Token { get; private set; } = "";
    public LinkFailure Failure { get; private set; }
    public bool NeedsPasscode { get; private set; }
    public bool WrongPasscode { get; private set; }
    public Memory? Memory { get; private set; }
    public List<Attachment> Items { get; private set; } = [];
    public List<string[]> Paragraphs { get; private set; } = [];

    public async Task<IActionResult> OnGetAsync(string token)
    {
        Response.Headers["X-Robots-Tag"] = "noindex, nofollow";
        Token = token;
        var resolution = await sharing.ResolveAsync(token);
        Failure = resolution.Failure;
        if (!resolution.Ok)
        {
            Response.StatusCode = StatusCodes.Status404NotFound;
            return Page();
        }
        var share = resolution.Share!;
        if (!sharing.IsUnlocked(share, Request.Cookies[sharing.UnlockCookieName(share)]))
        {
            NeedsPasscode = true;
            return Page();
        }
        await sharing.RecordUseAsync(share);
        Memory = await db.Memories.AsNoTracking().Include(m => m.Prompt).Include(m => m.Storyteller).Include(m => m.WrittenBy)
            .SingleAsync(m => m.Id == share.MemoryId);
        Items = await db.Attachments.AsNoTracking().Where(a => a.MemoryId == share.MemoryId && a.RemovedAt == null)
            .OrderBy(a => a.SortOrder).ThenBy(a => a.CreatedAt).ToListAsync();
        Paragraphs = [.. Memory.Body.Split("\n\n", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(p => p.Split('\n'))];
        return Page();
    }

    public async Task<IActionResult> OnPostAsync(string token, string? passcode)
    {
        Token = token;
        var resolution = await sharing.ResolveAsync(token);
        if (!resolution.Ok) return await OnGetAsync(token);
        var share = resolution.Share!;
        if (!sharing.CheckPasscode(share, passcode))
        {
            NeedsPasscode = true;
            WrongPasscode = true;
            return Page();
        }
        Response.Cookies.Append(sharing.UnlockCookieName(share), sharing.CreateUnlockValue(share), new CookieOptions
        {
            HttpOnly = true, Secure = Request.IsHttps, SameSite = SameSiteMode.Lax, Path = $"/s/{token}", MaxAge = TimeSpan.FromHours(12),
        });
        return LocalRedirect($"/s/{token}");
    }
}
