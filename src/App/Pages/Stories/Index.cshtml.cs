using FamilyMemories.Auth;
using FamilyMemories.Data;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace FamilyMemories.Pages.Stories;

public sealed class IndexModel(AppDbContext db) : PageModel
{
    public List<Memory> Stories { get; private set; } = [];

    public async Task OnGetAsync()
    {
        var me = User.UserId();
        Stories = await db.Memories.AsNoTracking()
            .Where(m => m.StorytellerId == me && (m.Status == MemoryStatus.Draft || m.Status == MemoryStatus.Complete))
            .OrderByDescending(m => m.UpdatedAt).ToListAsync();
    }
}
