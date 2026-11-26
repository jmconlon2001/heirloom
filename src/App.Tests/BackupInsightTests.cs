using System.IO.Compression;
using System.Net;
using FamilyMemories.Backup;
using FamilyMemories.Configuration;
using FamilyMemories.Data;
using FamilyMemories.Stories;
using FamilyMemories.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace FamilyMemories.Tests;

public sealed class BackupInsightUnitTests
{
    private const string MountInfo = """
        22 1 0:21 / / rw,relatime - overlay overlay rw
        30 22 8:1 /srv/disk1/appdata/heirloom/data /data rw,relatime - ext4 /dev/sda1 rw
        31 22 8:1 /srv/disk1/backups/heirloom /backups rw,relatime - ext4 /dev/sda1 rw
        32 22 8:17 /srv/disk2/backups /mnt/usb rw,relatime - ext4 /dev/sdb1 rw
        """;

    [Fact]
    public void Finds_the_disk_behind_a_folder_from_the_mount_table()
    {
        Assert.Equal("8:1", DiskCheck.DeviceOf("/data/media", MountInfo));
        Assert.Equal("8:1", DiskCheck.DeviceOf("/backups/restic", MountInfo));
        Assert.Equal("8:17", DiskCheck.DeviceOf("/mnt/usb/restic", MountInfo));
        Assert.Equal("0:21", DiskCheck.DeviceOf("/app", MountInfo));
        Assert.Equal("0:21", DiskCheck.DeviceOf("/databank", MountInfo));   // "/data" mustn't match "/databank"
    }

    [Fact]
    public void Same_disk_is_unknown_without_a_mount_table() =>
        Assert.Null(DiskCheck.SameDisk("/data", "/backups", "/no/such/mountinfo"));

    [Fact]
    public void Describes_backup_locations_in_plain_words_and_hides_passwords()
    {
        var conf = Path.GetTempFileName();
        File.WriteAllText(conf, "[onedrive]\ntype = onedrive\ntoken = {\"secret\":1}\n\n[gdrive]\ntype = drive\n");
        try
        {
            Assert.Equal("a folder", BackupLocations.Kind("/backups/restic", conf));
            Assert.Equal("Backblaze B2", BackupLocations.Kind("b2:bucket:heirloom", conf));
            Assert.Equal("OneDrive (through rclone)", BackupLocations.Kind("rclone:onedrive:heirloom", conf));
            Assert.Equal("Google Drive (through rclone)", BackupLocations.Kind("rclone:gdrive:backups/heirloom", conf));
            Assert.Equal("\"dropbox\", which isn't in rclone.conf yet (through rclone)", BackupLocations.Kind("rclone:dropbox:x", conf));
            Assert.Equal("rest:https://family:•••@backup.example.com/heirloom", BackupLocations.Mask("rest:https://family:hunter2@backup.example.com/heirloom"));
        }
        finally { File.Delete(conf); }
    }

    [Fact]
    public void Locations_show_the_servers_own_folder_names()
    {
        var o = AppOptions.FromConfiguration(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["BACKUP_ROOT_ON_HOST"] = "/srv/dev-disk-by-uuid-1/backups/heirloom/", ["PLAIN_COPY"] = "true",
            ["OFFSITE_RESTIC_REPOSITORY"] = "b2:family:heirloom",
        }).Build());
        var l = BackupLocations.Describe(o);
        Assert.Equal(3, l.Count);
        Assert.Equal("/srv/dev-disk-by-uuid-1/backups/heirloom/restic", l[0].OnHost);
        Assert.Equal(("Offsite copy", "Backblaze B2"), (l[1].Role, l[1].Kind));
        Assert.Equal("/srv/dev-disk-by-uuid-1/backups/heirloom/plain", l[2].OnHost);
    }

    [Fact]
    public void Mirror_copies_changes_and_removes_deleted_files()
    {
        var root = Path.Combine(Path.GetTempPath(), "fm-mirror-" + Guid.NewGuid().ToString("N"));
        var (src, dst) = (Path.Combine(root, "src"), Path.Combine(root, "dst"));
        try
        {
            Directory.CreateDirectory(Path.Combine(src, "a"));
            File.WriteAllText(Path.Combine(src, "a", "one.txt"), "one");
            File.WriteAllText(Path.Combine(src, "two.txt"), "two");
            BackupService.Mirror(src, dst);
            Assert.Equal("one", File.ReadAllText(Path.Combine(dst, "a", "one.txt")));

            File.Delete(Path.Combine(src, "a", "one.txt"));
            File.WriteAllText(Path.Combine(src, "two.txt"), "two, changed");
            BackupService.Mirror(src, dst);
            Assert.False(Directory.Exists(Path.Combine(dst, "a")));
            Assert.Equal("two, changed", File.ReadAllText(Path.Combine(dst, "two.txt")));
        }
        finally { Directory.Delete(root, recursive: true); }
    }
}

public sealed class PlainCopyFactory(PostgresFixture postgres) : BackupFactory(postgres)
{
    public string PlainDir { get; } = Path.Combine(Path.GetTempPath(), "fm-plain", Guid.NewGuid().ToString("N"));

    protected override IDictionary<string, string?> Settings => new Dictionary<string, string?>(base.Settings)
    {
        ["PLAIN_COPY"] = "true",
        ["PLAIN_COPY_DIR"] = PlainDir,
    };

    public override async ValueTask DisposeAsync()
    {
        await base.DisposeAsync();
        try { Directory.Delete(PlainDir, recursive: true); } catch (IOException) { }
    }
}

public sealed class BackupInsightTests(PostgresFixture postgres) : IAsyncLifetime
{
    private readonly PlainCopyFactory app = new(postgres);
    public ValueTask InitializeAsync() => app.InitializeAsync();
    public ValueTask DisposeAsync() => app.DisposeAsync();

    [Fact]
    public async Task Backup_records_what_it_held_and_keeps_a_plain_copy()
    {
        var rose = await app.CreateUserAsync("Rose", "plain-rose@example.test", [Roles.Storyteller], PromptSetKind.Core);
        await using (var scope = app.Services.CreateAsyncScope())
        {
            var memories = scope.ServiceProvider.GetRequiredService<MemoryService>();
            var memory = await memories.CreateFreeFormAsync(rose.Id, rose.Id);
            await memories.SaveAsync(memory.Id, new MemoryEdit("The county fair", "Joe pinned the ribbon on my coat.", default, null), rose.Id, VersionKind.Save);
            var attachments = scope.ServiceProvider.GetRequiredService<FamilyMemories.Media.AttachmentService>();
            await using var photo = File.OpenRead(AppFactory.Fixture("photo.heic"));
            await attachments.AddFromStreamAsync(memory.Id, "photo.heic", photo, rose.Id);
        }

        var result = await app.Services.GetRequiredService<BackupService>().BackupAsync(BackupTrigger.Manual);
        Assert.True(result.Succeeded, result.Message);

        var manifest = await app.WithDbAsync(BackupManifest.LoadAsync);
        Assert.NotNull(manifest);
        Assert.Equal(1, manifest.Counts["stories"]);
        Assert.Equal(1, manifest.Counts["photos and files"]);
        Assert.True(manifest.DatabaseBytes > 0 && manifest.MediaFiles >= 1);
        Assert.Null(manifest.PlainCopyError);

        // The plain copy opens with ordinary tools: gzip'd SQL with the story in it, and the media files.
        await using (var gz = new GZipStream(File.OpenRead(Path.Combine(app.PlainDir, "heirloom.sql.gz")), CompressionMode.Decompress))
        using (var reader = new StreamReader(gz))
            Assert.Contains("Joe pinned the ribbon on my coat.", await reader.ReadToEndAsync());
        Assert.NotEmpty(Directory.EnumerateFiles(Path.Combine(app.PlainDir, "media"), "*", SearchOption.AllDirectories));
        Assert.Contains("NOT encrypted", await File.ReadAllTextAsync(Path.Combine(app.PlainDir, "README.txt")));

        var admin = await Browser.SignedInAsync(app, AppFactory.AdminEmail);
        var page = WebUtility.HtmlDecode(await admin.Http.GetStringAsync("/admin/backups"));
        Assert.Contains("What's in each backup", page);
        Assert.Contains("1 story, 1 photo or file", page);
        Assert.Contains("A plain copy (heirloom.sql.gz and the media folder)", page);
        Assert.Contains("Plain copy (no restic needed to open it)", page);
        Assert.Contains("There's no offsite copy yet.", page);
    }
}
