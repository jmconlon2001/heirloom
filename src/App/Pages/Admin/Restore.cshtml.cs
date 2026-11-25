using FamilyMemories.Admin;
using FamilyMemories.Auth;
using FamilyMemories.Backup;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace FamilyMemories.Pages.Admin;

public sealed class RestoreModel(BackupService backups, IBackupQueue queue, AuditLog audit) : PageModel
{
    public Snapshot? Snapshot { get; private set; }
    public string? Error { get; private set; }

    public async Task OnGetAsync(string snapshotId) =>
        Snapshot = (await backups.ListSnapshotsAsync()).FirstOrDefault(s => s.Id == snapshotId);

    public async Task<IActionResult> OnPostAsync(string snapshotId, string? confirm)
    {
        Snapshot = (await backups.ListSnapshotsAsync()).FirstOrDefault(s => s.Id == snapshotId);
        if (Snapshot is null) return Page();
        if (confirm?.Trim() != "RESTORE")
        {
            Error = "Please type RESTORE (in capital letters) to confirm.";
            return Page();
        }
        await audit.WriteAsync("backup.restore-start", $"Started restoring backup {Snapshot.ShortId}", "snapshot", Snapshot.Id);
        await queue.RestoreAsync(Snapshot.Id, User.UserId());
        TempData["Flash"] = "Restore started. The site will be back in a few minutes.";
        return LocalRedirect("/admin/backups");
    }
}
