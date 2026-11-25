using System.Net;
using System.Security.Cryptography;
using FamilyMemories.Backup;
using FamilyMemories.Configuration;
using FamilyMemories.Data;
using FamilyMemories.Media;
using FamilyMemories.Stories;
using FamilyMemories.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace FamilyMemories.Tests;

/// <summary>A factory with restic backups switched on, writing to a temporary repository.</summary>
public class BackupFactory(PostgresFixture postgres, string password = "correct horse battery staple") : AppFactory(postgres)
{
    public string Repository { get; } = Path.Combine(Path.GetTempPath(), "fm-restic", Guid.NewGuid().ToString("N"));

    /// <summary>PG_BIN_DIR from the environment (CI), else Homebrew's postgresql@17 if present, else PATH.</summary>
    public static string PgBinDir =>
        Environment.GetEnvironmentVariable("PG_BIN_DIR") is { Length: > 0 } dir ? dir
        : Directory.Exists("/opt/homebrew/opt/postgresql@17/bin") ? "/opt/homebrew/opt/postgresql@17/bin"
        : "";

    protected override IDictionary<string, string?> Settings => new Dictionary<string, string?>
    {
        ["RESTIC_PASSWORD"] = password,
        ["RESTIC_REPOSITORY"] = Repository,
        ["PG_BIN_DIR"] = PgBinDir,
    };

    public override async ValueTask DisposeAsync()
    {
        await base.DisposeAsync();
        try { Directory.Delete(Repository, recursive: true); } catch (IOException) { }
    }
}

public sealed class BackupRestoreTests(PostgresFixture postgres) : IAsyncLifetime
{
    private readonly BackupFactory app = new(postgres);
    public ValueTask InitializeAsync() => app.InitializeAsync();
    public ValueTask DisposeAsync() => app.DisposeAsync();

    private static readonly string[] CheckedTables =
        ["AspNetUsers", "AspNetUserRoles", "Prompts", "Chapters", "Memories", "MemoryVersions", "Attachments", "UserDevices", "StorytellerHelpers"];

    private BackupService Backups => app.Services.GetRequiredService<BackupService>();
    private AppOptions Options => app.Services.GetRequiredService<AppOptions>();

    private async Task SeedFamilyAsync()
    {
        var rose = await app.CreateUserAsync("Rose", "backup-rose@example.test", [Roles.Storyteller], PromptSetKind.Core);
        await using var scope = app.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var memories = scope.ServiceProvider.GetRequiredService<MemoryService>();
        var attachments = scope.ServiceProvider.GetRequiredService<AttachmentService>();

        foreach (var key in new[] { "roots-born", "roots-home", "roots-parents" })
        {
            var prompt = await db.Prompts.SingleAsync(p => p.Key == key);
            var memory = await memories.GetOrCreateForPromptAsync(rose.Id, prompt, rose.Id);
            await memories.SaveAsync(memory.Id, new MemoryEdit(null, $"Story for {key}.\n\nWith two paragraphs.", default, "Akron"), rose.Id, VersionKind.Save);
            await memories.SetStatusAsync(memory.Id, MemoryStatus.Complete, rose.Id);
            foreach (var fixture in new[] { "photo.heic", "voice-safari.mp4" })
            {
                await using var stream = File.OpenRead(AppFactory.Fixture(fixture));
                var (_, error) = await attachments.AddFromStreamAsync(memory.Id, fixture, stream, rose.Id);
                Assert.Equal(UploadError.None, error);
            }
        }
    }

    private async Task<Dictionary<string, string>> MediaChecksumsAsync()
    {
        var result = new Dictionary<string, string>();
        foreach (var file in Directory.EnumerateFiles(Options.MediaDir, "*", SearchOption.AllDirectories))
        {
            await using var s = File.OpenRead(file);
            result[Path.GetRelativePath(Options.MediaDir, file)] = Convert.ToHexString(await SHA256.HashDataAsync(s));
        }
        return result;
    }

    private async Task<Dictionary<string, long>> RowCountsAsync()
    {
        var counts = new Dictionary<string, long>();
        await using var conn = new NpgsqlConnection(Options.ConnectionString);
        await conn.OpenAsync();
        foreach (var table in CheckedTables)
        {
            await using var cmd = new NpgsqlCommand($"SELECT count(*) FROM \"{table}\"", conn);
            counts[table] = (long)(await cmd.ExecuteScalarAsync())!;
        }
        return counts;
    }

    [Fact]
    public async Task Backup_wipe_restore_brings_back_identical_files_and_rows()
    {
        await SeedFamilyAsync();
        var filesBefore = await MediaChecksumsAsync();
        var rowsBefore = await RowCountsAsync();
        Assert.Equal(15, filesBefore.Count); // 3 stories × (heic original, web, thumb, mp4 original, m4a)
        Assert.Equal(3, rowsBefore["Memories"]);

        var backup = await Backups.BackupAsync(BackupTrigger.Manual);
        Assert.True(backup.Succeeded, backup.Message);
        var snapshot = Assert.Single(await Backups.ListSnapshotsAsync());
        Assert.Equal(backup.SnapshotId, snapshot.Id);
        Assert.Contains("manual", snapshot.Tags);

        // Disaster: the database and every photo are gone.
        Directory.Delete(Options.MediaDir, recursive: true);
        await using (var conn = new NpgsqlConnection(Options.ConnectionString))
        {
            await conn.OpenAsync();
            await new NpgsqlCommand("DROP SCHEMA public CASCADE; CREATE SCHEMA public;", conn).ExecuteNonQueryAsync();
        }
        NpgsqlConnection.ClearAllPools();

        var restore = await Backups.RestoreAsync(snapshot.Id);
        Assert.True(restore.Succeeded, restore.Message);

        Assert.Equal(filesBefore, await MediaChecksumsAsync());
        Assert.Equal(rowsBefore, await RowCountsAsync());
        var body = await app.WithDbAsync(db => db.Memories.Select(m => m.Body).FirstAsync());
        Assert.Contains("With two paragraphs.", body);

        // The restore itself is recorded, and a safety backup was taken first.
        Assert.True(await app.WithDbAsync(db => db.BackupRuns.AnyAsync(r => r.Kind == BackupKind.Restore && r.Outcome == BackupOutcome.Succeeded)));
        var snapshots = await Backups.ListSnapshotsAsync();
        Assert.Contains(snapshots, s => s.Tags.Contains("prerestore"));
        Assert.False(app.Services.GetRequiredService<MaintenanceMode>().IsOn);

        // The site works afterwards: Rose can sign in and read her story.
        var rose = await Browser.SignedInAsync(app, "backup-rose@example.test");
        Assert.Contains("You've shared <strong>3 stories</strong>", WebUtility.HtmlDecode(await rose.Http.GetStringAsync("/")));
    }

    [Fact]
    public async Task Weekly_check_reads_the_repository_and_test_restores_the_database()
    {
        await SeedFamilyAsync();
        Assert.True((await Backups.BackupAsync(BackupTrigger.Nightly)).Succeeded);

        var verify = await Backups.VerifyAsync(BackupTrigger.Weekly);
        Assert.True(verify.Succeeded, verify.Message);
        Assert.Contains("3 Memories", verify.Message);

        // The throwaway database is gone afterwards.
        await using var conn = new NpgsqlConnection(new NpgsqlConnectionStringBuilder(Options.ConnectionString) { Database = "postgres" }.ConnectionString);
        await conn.OpenAsync();
        var leftovers = (long)(await new NpgsqlCommand("SELECT count(*) FROM pg_database WHERE datname LIKE 'fm_verify_%'", conn).ExecuteScalarAsync())!;
        Assert.Equal(0, leftovers);

        var health = await app.WithServiceAsync<BackupStatusQuery, BackupHealth>(s => s.GetAsync());
        Assert.True(health.Healthy(DateTime.UtcNow));
        Assert.NotNull(health.LastGoodVerify);
    }

    [Fact]
    public async Task A_check_with_no_backups_fails_and_emails_the_admins()
    {
        var verify = await Backups.VerifyAsync(BackupTrigger.Weekly);
        Assert.False(verify.Succeeded);
        var alert = Assert.Single(app.Email.To(AppFactory.AdminEmail));
        Assert.Contains("Backup check failed", alert.Subject);

        var health = await app.WithServiceAsync<BackupStatusQuery, BackupHealth>(s => s.GetAsync());
        Assert.False(health.Healthy(DateTime.UtcNow));
    }

    [Fact]
    public async Task The_admin_page_shows_status_backs_up_on_request_and_asks_about_the_password_once()
    {
        var admin = await Browser.SignedInAsync(app, AppFactory.AdminEmail);
        var page = WebUtility.HtmlDecode(await admin.Http.GetStringAsync("/admin/backups"));
        Assert.Contains("Please save the backup password", page);
        Assert.Contains("Last good backup:", page);
        Assert.Contains("none yet", page);

        await admin.Http.PostFormAsync("/admin/backups?handler=BackupNow", page);
        page = WebUtility.HtmlDecode(await admin.Http.GetStringAsync("/admin/backups"));
        Assert.Contains("Restore this one", page);
        Assert.DoesNotContain("none yet</strong>\n        </p>\n        <p class=\"backup-line\">\n            Last successful check", page);

        await admin.Http.PostFormAsync("/admin/backups?handler=DismissBanner", page);
        Assert.DoesNotContain("Please save the backup password", await admin.Http.GetStringAsync("/admin/backups"));

        // The admin home page shows the last backup prominently.
        Assert.Contains("Last good backup:", await admin.Http.GetStringAsync("/"));
    }

    [Fact]
    public async Task Restoring_needs_the_word_RESTORE()
    {
        Assert.True((await Backups.BackupAsync(BackupTrigger.Manual)).Succeeded);
        var snapshot = Assert.Single(await Backups.ListSnapshotsAsync());
        var admin = await Browser.SignedInAsync(app, AppFactory.AdminEmail);
        var page = await admin.Http.GetStringAsync($"/admin/backups/restore/{snapshot.Id}");

        var wrong = await admin.Http.PostFormAsync($"/admin/backups/restore/{snapshot.Id}", page, ("Confirm", "restore please"));
        Assert.Contains("Please type RESTORE", WebUtility.HtmlDecode(await wrong.Content.ReadAsStringAsync()));
        Assert.False(await app.WithDbAsync(db => db.BackupRuns.AnyAsync(r => r.Kind == BackupKind.Restore)));
    }
}

public sealed class BackupFailureTests(PostgresFixture postgres) : IAsyncLifetime
{
    private readonly BackupFactory app = new(postgres);
    public ValueTask InitializeAsync() => app.InitializeAsync();
    public ValueTask DisposeAsync() => app.DisposeAsync();

    [Fact]
    public async Task A_failed_backup_is_recorded_and_every_admin_is_emailed()
    {
        // Someone created the repository with a different password (e.g. .env was replaced).
        Directory.CreateDirectory(app.Repository);
        var init = await FamilyMemories.Media.ExternalTool.RunAsync("restic", ["init"], TimeSpan.FromMinutes(1), default,
            new Dictionary<string, string> { ["RESTIC_REPOSITORY"] = app.Repository, ["RESTIC_PASSWORD"] = "a different password" });
        Assert.True(init.Succeeded, init.StdErr);

        var result = await app.Services.GetRequiredService<BackupService>().BackupAsync(BackupTrigger.Nightly);
        Assert.False(result.Succeeded);
        Assert.Contains("Could not open the backup repository", result.Message);
        var run = await app.WithDbAsync(db => db.BackupRuns.SingleAsync());
        Assert.Equal(BackupOutcome.Failed, run.Outcome);
        Assert.Contains("Backup failed", Assert.Single(app.Email.To(AppFactory.AdminEmail)).Subject);
    }
}

public sealed class MaintenanceTests(PostgresFixture postgres) : IAsyncLifetime
{
    private readonly AppFactory app = new(postgres);
    public ValueTask InitializeAsync() => app.InitializeAsync();
    public ValueTask DisposeAsync() => app.DisposeAsync();

    [Fact]
    public async Task During_a_restore_everyone_sees_a_calm_page_but_health_checks_answer()
    {
        var maintenance = app.Services.GetRequiredService<MaintenanceMode>();
        maintenance.Enter("We're restoring the family's stories from a backup.");
        try
        {
            var browser = new Browser(app);
            var response = await browser.Http.GetAsync("/account/login");
            Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
            var html = WebUtility.HtmlDecode(await response.Content.ReadAsStringAsync());
            Assert.Contains("Back in a few minutes", html);
            Assert.Contains("Your stories are safe", html);
            Assert.Equal(HttpStatusCode.OK, (await browser.Http.GetAsync("/healthz")).StatusCode);
        }
        finally
        {
            maintenance.Exit();
        }
        Assert.Equal(HttpStatusCode.OK, (await new Browser(app).Http.GetAsync("/account/login")).StatusCode);
    }
}
