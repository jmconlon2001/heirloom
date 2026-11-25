using FamilyMemories.Auth;
using FamilyMemories.Data;
using FamilyMemories.Stories;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace FamilyMemories.Pages;

public sealed class IndexModel(AppDbContext db, StoryPlanService plans, StoryAccess access, FamilyMemories.Backup.BackupStatusQuery backups) : PageModel
{
    public FamilyMemories.Backup.BackupHealth? Backups { get; private set; }
    public StoryPlan? Plan { get; private set; }
    public string FirstName { get; private set; } = "";
    public List<AppUser> WriteFor { get; private set; } = [];

    public async Task OnGetAsync()
    {
        var me = User.UserId();
        var user = await db.Users.AsNoTracking().SingleAsync(u => u.Id == me);
        FirstName = user.DisplayName.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? user.DisplayName;
        if (user.PromptSetId is not null) Plan = await plans.GetAsync(me);
        if (User.IsAdmin()) Backups = await backups.GetAsync();
        WriteFor = [.. (await access.WritableStorytellersAsync(User)).Where(u => u.Id != me)];
    }
}
