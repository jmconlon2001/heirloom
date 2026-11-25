using FamilyMemories.Data;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace FamilyMemories.Pages.Admin;

public sealed class AuditModel(AppDbContext db) : PageModel
{
    public int PageSize => 100;
    public List<AuditEntry> Entries { get; private set; } = [];

    public async Task OnGetAsync(long? before) =>
        Entries = await db.AuditLog.AsNoTracking()
            .Where(e => before == null || e.Id < before)
            .OrderByDescending(e => e.Id).Take(PageSize).ToListAsync();
}
