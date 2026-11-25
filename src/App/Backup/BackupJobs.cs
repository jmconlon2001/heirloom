using FamilyMemories.Configuration;
using FamilyMemories.Data;
using Hangfire;

namespace FamilyMemories.Backup;

/// <summary>Entry points for scheduled and button-triggered backup work.</summary>
public sealed class BackupJobs(BackupService backups)
{
    [AutomaticRetry(Attempts = 0)]
    [DisableConcurrentExecution(timeoutInSeconds: 60)]
    public Task NightlyAsync() => backups.BackupAsync(BackupTrigger.Nightly);

    [AutomaticRetry(Attempts = 0)]
    [DisableConcurrentExecution(timeoutInSeconds: 60)]
    public Task WeeklyVerifyAsync() => backups.VerifyAsync(BackupTrigger.Weekly);

    [AutomaticRetry(Attempts = 0)]
    public Task ManualBackupAsync(Guid? startedBy) => backups.BackupAsync(BackupTrigger.Manual, startedBy);

    [AutomaticRetry(Attempts = 0)]
    public Task ManualVerifyAsync(Guid? startedBy) => backups.VerifyAsync(BackupTrigger.Manual, startedBy);

    [AutomaticRetry(Attempts = 0)]
    public Task RestoreAsync(string snapshotId, Guid? startedBy) => backups.RestoreAsync(snapshotId, startedBy);

    public static void Schedule(IRecurringJobManager recurring, AppOptions options)
    {
        var (hour, minute) = ParseTime(options.BackupTime);
        var opts = new RecurringJobOptions { TimeZone = options.TimeZone };
        recurring.AddOrUpdate<BackupJobs>("backup-nightly", j => j.NightlyAsync(), Cron.Daily(hour, minute), opts);
        recurring.AddOrUpdate<BackupJobs>("backup-verify-weekly", j => j.WeeklyVerifyAsync(), Cron.Weekly(DayOfWeek.Sunday, (hour + 2) % 24, minute), opts);
    }

    internal static (int Hour, int Minute) ParseTime(string value) =>
        TimeOnly.TryParse(value, out var t) ? (t.Hour, t.Minute) : (2, 30);
}

/// <summary>Runs "Back up now", "Check now" and restores: on Hangfire, or straight away when jobs are off.</summary>
public interface IBackupQueue
{
    Task BackupAsync(Guid? startedBy);
    Task VerifyAsync(Guid? startedBy);
    Task RestoreAsync(string snapshotId, Guid? startedBy);
}

public sealed class HangfireBackupQueue(IBackgroundJobClient jobs) : IBackupQueue
{
    public Task BackupAsync(Guid? startedBy) { jobs.Enqueue<BackupJobs>(j => j.ManualBackupAsync(startedBy)); return Task.CompletedTask; }
    public Task VerifyAsync(Guid? startedBy) { jobs.Enqueue<BackupJobs>(j => j.ManualVerifyAsync(startedBy)); return Task.CompletedTask; }
    public Task RestoreAsync(string snapshotId, Guid? startedBy) { jobs.Enqueue<BackupJobs>(j => j.RestoreAsync(snapshotId, startedBy)); return Task.CompletedTask; }
}

public sealed class InlineBackupQueue(BackupService backups) : IBackupQueue
{
    public Task BackupAsync(Guid? startedBy) => backups.BackupAsync(BackupTrigger.Manual, startedBy);
    public Task VerifyAsync(Guid? startedBy) => backups.VerifyAsync(BackupTrigger.Manual, startedBy);
    public Task RestoreAsync(string snapshotId, Guid? startedBy) => backups.RestoreAsync(snapshotId, startedBy);
}
