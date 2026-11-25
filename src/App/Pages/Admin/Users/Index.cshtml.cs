using FamilyMemories.Data;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace FamilyMemories.Pages.Admin.Users;

public sealed class IndexModel(AppDbContext db, TimeProvider clock) : PageModel
{
    public sealed record Row(AppUser User, List<string> Roles, int Shared, bool Paused);
    public List<Row> Rows { get; private set; } = [];

    public async Task OnGetAsync()
    {
        var users = await db.Users.AsNoTracking().Include(u => u.PromptSet).OrderBy(u => u.DisplayName).ToListAsync();
        var roles = await (from ur in db.UserRoles join r in db.Roles on ur.RoleId equals r.Id select new { ur.UserId, r.Name })
            .ToListAsync();
        var shared = await db.Memories.Where(m => m.Status == MemoryStatus.Complete)
            .GroupBy(m => m.StorytellerId).Select(g => new { g.Key, Count = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.Count);
        var now = clock.GetUtcNow();
        Rows = [.. users.Select(u => new Row(u,
            [.. Roles.All.Where(r => roles.Any(x => x.UserId == u.Id && x.Name == r))],
            shared.GetValueOrDefault(u.Id),
            u.LockoutEnd > now))];
    }
}
