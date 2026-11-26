using FamilyMemories.Auth;
using FamilyMemories.Data;
using FamilyMemories.Stories;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace FamilyMemories.Pages;

public sealed class IndexModel(AppDbContext db, StoryPlanService plans, StoryAccess access, FamilyMemories.Backup.BackupStatusQuery backups, LibraryService library,
    FamilyMemories.Configuration.AppOptions options, TimeProvider clock) : PageModel
{
    public string ReminderLine { get; private set; } = "";
    public List<StoryCard> Newest { get; private set; } = [];
    public FamilyMemories.Backup.BackupHealth? Backups { get; private set; }
    public StoryPlan? Plan { get; private set; }
    public string FirstName { get; private set; } = "";
    public List<AppUser> WriteFor { get; private set; } = [];

    /// <summary>Signed-out visitors see the public landing page.</summary>
    public bool Landing { get; private set; }

    public async Task OnGetAsync()
    {
        if (User.Identity?.IsAuthenticated != true)
        {
            Landing = true;
            return;
        }
        var me = User.UserId();
        var user = await db.Users.AsNoTracking().SingleAsync(u => u.Id == me);
        FirstName = user.DisplayName.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? user.DisplayName;
        if (user.PromptSetId is not null)
        {
            Plan = await plans.GetAsync(me);
            var settings = await db.ReminderSettings.AsNoTracking().SingleOrDefaultAsync(r => r.UserId == me);
            ReminderLine = await FamilyMemories.Reminders.ReminderText.DescribeAsync(db, options, settings, user, clock.GetUtcNow().UtcDateTime);
        }
        if (User.IsAdmin()) Backups = await backups.GetAsync();
        Newest = await library.NewestAsync(User, 6);
        WriteFor = [.. (await access.WritableStorytellersAsync(User)).Where(u => u.Id != me)];
    }
}
