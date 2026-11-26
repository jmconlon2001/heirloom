using FamilyMemories.Admin;
using FamilyMemories.Auth;
using FamilyMemories.Backup;
using FamilyMemories.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace FamilyMemories.Pages.Admin;

public sealed class BackupsModel(AppDbContext db, BackupStatusQuery status, BackupService backups, IBackupQueue queue, AuditLog audit,
    FamilyMemories.Configuration.AppOptions options, TimeProvider clock) : PageModel
{
    public BackupManifest? Manifest { get; private set; }
    public IReadOnlyList<BackupLocation> Locations { get; private set; } = [];
    /// <summary>The local backups are on the same disk as the stories (null when it can't tell).</summary>
    public bool? SameDisk { get; private set; }
    public BackupHealth Health { get; private set; } = null!;
    public List<BackupRun> Runs { get; private set; } = [];
    public List<Snapshot> Snapshots { get; private set; } = [];

    public async Task OnGetAsync()
    {
        Health = await status.GetAsync();
        Runs = await db.BackupRuns.AsNoTracking().OrderByDescending(r => r.StartedAt).Take(20).ToListAsync();
        Snapshots = [.. (await backups.ListSnapshotsAsync()).Take(40)];
        Manifest = await BackupManifest.LoadAsync(db);
        Locations = BackupLocations.Describe(options);
        SameDisk = BackupLocations.IsLocal(options.ResticRepository) ? DiskCheck.SameDisk(options.DataDir, options.ResticRepository) : false;
    }

    public async Task<IActionResult> OnPostBackupNowAsync()
    {
        await audit.WriteAsync("backup.manual", "Started a backup");
        await queue.BackupAsync(User.UserId());
        TempData["Flash"] = "Backup started. It usually takes a minute or two; refresh this page to see the result.";
        return LocalRedirect("/admin/backups");
    }

    public async Task<IActionResult> OnPostVerifyNowAsync()
    {
        await queue.VerifyAsync(User.UserId());
        TempData["Flash"] = "Check started. Refresh this page in a few minutes to see the result.";
        return LocalRedirect("/admin/backups");
    }

    public async Task<IActionResult> OnPostDismissBannerAsync()
    {
        if (!await db.Settings.AnyAsync(s => s.Key == BackupStatusQuery.PasswordBannerKey))
            db.Settings.Add(new AppSetting { Key = BackupStatusQuery.PasswordBannerKey, Value = User.UserId().ToString(), UpdatedAt = clock.GetUtcNow().UtcDateTime });
        audit.Add("backup.password-acknowledged", "Confirmed the backup password is stored safely");
        await db.SaveChangesAsync();
        return LocalRedirect("/admin/backups");
    }
}
