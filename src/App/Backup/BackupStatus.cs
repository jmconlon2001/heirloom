using FamilyMemories.Configuration;
using FamilyMemories.Data;
using Microsoft.EntityFrameworkCore;

namespace FamilyMemories.Backup;

public sealed record BackupHealth(
    bool Configured,
    BackupRun? LastBackup,
    BackupRun? LastGoodBackup,
    BackupRun? LastVerify,
    BackupRun? LastGoodVerify,
    bool ShowPasswordBanner,
    bool Running)
{
    /// <summary>Everything fine: a good backup in the last 36 hours, and the last attempts didn't fail.</summary>
    public bool Healthy(DateTime now) => Configured && LastGoodBackup is not null
        && now - LastGoodBackup.FinishedAt!.Value < TimeSpan.FromHours(36)
        && LastBackup?.Outcome != BackupOutcome.Failed && LastVerify?.Outcome != BackupOutcome.Failed;
}

public sealed class BackupStatusQuery(AppDbContext db, AppOptions options)
{
    public const string PasswordBannerKey = "backup.password-banner-dismissed";

    public async Task<BackupHealth> GetAsync()
    {
        var runs = await db.BackupRuns.AsNoTracking().Where(r => r.Kind != BackupKind.Restore)
            .OrderByDescending(r => r.StartedAt).Take(100).ToListAsync();
        BackupRun? Latest(BackupKind k, bool goodOnly) => runs.FirstOrDefault(r => r.Kind == k
            && r.Outcome != BackupOutcome.Running && (!goodOnly || r.Outcome == BackupOutcome.Succeeded));
        var dismissed = await db.Settings.AnyAsync(s => s.Key == PasswordBannerKey);
        return new BackupHealth(
            options.BackupsConfigured,
            Latest(BackupKind.Backup, false), Latest(BackupKind.Backup, true),
            Latest(BackupKind.Verify, false), Latest(BackupKind.Verify, true),
            options.BackupsConfigured && !dismissed,
            runs.Any(r => r.Outcome == BackupOutcome.Running && r.StartedAt > DateTime.UtcNow.AddHours(-6)));
    }
}
