using System.Security.Claims;
using FamilyMemories.Admin;
using FamilyMemories.Auth;
using FamilyMemories.Data;
using Microsoft.EntityFrameworkCore;

namespace FamilyMemories.Stories;

/// <summary>
/// Who may write and read a story, enforced in the query layer: every page, API, search and
/// /media request filters through <see cref="Readable"/>, never just the UI.
///
/// Reading (BUILD_PROMPT §3):
///  - the storyteller, whoever wrote it down, and their helpers: always (drafts too);
///  - everyone else only once it's finished, and then by visibility:
///    All family = every signed-in person; Selected people = those chosen; Private / Link = nobody
///    (Link stories are read through their link instead);
///  - admins: everything, always. Admin views that the rules above wouldn't allow are audit-logged.
/// </summary>
public sealed class StoryAccess(AppDbContext db, AuditLog audit)
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

    /// <summary>Stories this person may open. Admins: all.</summary>
    public IQueryable<Memory> Readable(ClaimsPrincipal user) =>
        user.IsAdmin() ? db.Memories : ReadableByRules(user.UserId());

    /// <summary>The non-admin rules, also used to decide whether an admin's view must be audited.</summary>
    public IQueryable<Memory> ReadableByRules(Guid me) =>
        db.Memories.Where(m =>
            m.StorytellerId == me
            || m.WrittenById == me
            || db.StorytellerHelpers.Any(h => h.StorytellerId == m.StorytellerId && h.HelperId == me)
            || (m.Status == MemoryStatus.Complete
                && (m.Visibility == Visibility.AllFamily
                    || (m.Visibility == Visibility.SelectedPeople && db.MemoryShares.Any(s => s.MemoryId == m.Id && s.UserId == me)))));

    /// <summary>What appears in the Family Library: finished stories this person may read.</summary>
    public IQueryable<Memory> Library(ClaimsPrincipal user) =>
        Readable(user).Where(m => m.Status == MemoryStatus.Complete);

    public async Task<bool> CanViewAsync(ClaimsPrincipal user, Memory memory)
    {
        if (user.Identity?.IsAuthenticated != true) return false;
        return await Readable(user).AnyAsync(m => m.Id == memory.Id);
    }

    /// <summary>Call when a story page is shown: audit-logs admin views beyond the normal rules (§3).</summary>
    public async Task RecordViewAsync(ClaimsPrincipal user, Memory memory, TimeProvider clock)
    {
        var me = user.UserId();
        if (user.IsAdmin() && !await ReadableByRules(me).AnyAsync(m => m.Id == memory.Id))
        {
            audit.Add("memory.admin-view", $"Admin opened \"{memory.Title}\" ({memory.Visibility}, {memory.Status}) by storyteller {memory.StorytellerId}", "memory", memory.Id);
        }
        if (me != memory.StorytellerId && memory.Status == MemoryStatus.Complete
            && !await db.Reads.AnyAsync(r => r.MemoryId == memory.Id && r.UserId == me))
        {
            db.Reads.Add(new MemoryRead { MemoryId = memory.Id, UserId = me, FirstReadAt = clock.GetUtcNow().UtcDateTime });
        }
        await db.SaveChangesAsync();
    }

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
