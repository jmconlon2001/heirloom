using System.Security.Claims;
using FamilyMemories.Auth;
using FamilyMemories.Data;
using Microsoft.EntityFrameworkCore;

namespace FamilyMemories.Stories;

/// <summary>
/// Who may write and read a story. Enforced in the query layer: every page and API that loads
/// a memory goes through here, not just the UI. Phase 3 adds sharing to <see cref="CanViewAsync"/>.
/// </summary>
public sealed class StoryAccess(AppDbContext db)
{
    /// <summary>The storyteller, an admin, or someone the admin named as a helper for that storyteller.</summary>
    public async Task<bool> CanWriteForAsync(ClaimsPrincipal user, Guid storytellerId)
    {
        if (user.Identity?.IsAuthenticated != true) return false;
        var me = user.UserId();
        if (me == storytellerId || user.IsAdmin()) return true;
        return await db.StorytellerHelpers.AnyAsync(h => h.StorytellerId == storytellerId && h.HelperId == me);
    }

    public Task<bool> CanEditAsync(ClaimsPrincipal user, Memory memory) => CanWriteForAsync(user, memory.StorytellerId);

    /// <summary>Phase 1: anyone who can edit it. Admins can always see everything.</summary>
    public Task<bool> CanViewAsync(ClaimsPrincipal user, Memory memory) => CanEditAsync(user, memory);

    /// <summary>Storytellers this user may write for (themself first, if they are one).</summary>
    public async Task<List<AppUser>> WritableStorytellersAsync(ClaimsPrincipal user)
    {
        var me = user.UserId();
        IQueryable<AppUser> query = user.IsAdmin()
            ? db.Users.Where(u => u.PromptSetId != null || u.Id == me)
            : db.Users.Where(u => u.Id == me || db.StorytellerHelpers.Any(h => h.HelperId == me && h.StorytellerId == u.Id));
        var list = await query.AsNoTracking().OrderBy(u => u.DisplayName).ToListAsync();
        return [.. list.OrderBy(u => u.Id == me ? 0 : 1)];
    }
}
