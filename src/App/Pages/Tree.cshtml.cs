using FamilyMemories.Auth;
using FamilyMemories.Data;
using FamilyMemories.Family;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace FamilyMemories.Pages;

public sealed class TreeModel(AppDbContext db, PeopleService people) : PageModel
{
    public Kin? Focus { get; private set; }

    public async Task OnGetAsync(Guid? focus)
    {
        var me = User.UserId();
        var id = focus
            ?? await db.People.Where(p => p.LinkedUserId == me).Select(p => (Guid?)p.Id).FirstOrDefaultAsync()
            ?? await db.People.OrderBy(p => p.CreatedAt).Select(p => (Guid?)p.Id).FirstOrDefaultAsync();
        if (id is not null) Focus = await people.KinAsync(id.Value);
    }
}
