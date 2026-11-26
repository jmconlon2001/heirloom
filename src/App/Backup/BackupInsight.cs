using System.Text.Json;
using System.Text.RegularExpressions;
using FamilyMemories.Configuration;
using FamilyMemories.Data;
using Microsoft.EntityFrameworkCore;

namespace FamilyMemories.Backup;

/// <summary>
/// What the last successful backup held, in numbers people can check: written after each backup and shown on the
/// Backups page ("What's in each backup").
/// </summary>
public sealed record BackupManifest(
    DateTime At,
    long DatabaseBytes,
    int MediaFiles,
    long MediaBytes,
    int KeyFiles,
    int ConfigFiles,
    IReadOnlyDictionary<string, int> Counts,
    string? PlainCopyAt,
    string? PlainCopyError)
{
    public const string SettingKey = "backup.manifest";

    public static async Task<BackupManifest?> LoadAsync(AppDbContext db)
    {
        var row = await db.Settings.AsNoTracking().SingleOrDefaultAsync(s => s.Key == SettingKey);
        return row is null ? null : JsonSerializer.Deserialize<BackupManifest>(row.Value);
    }

    public async Task SaveAsync(AppDbContext db)
    {
        var row = await db.Settings.SingleOrDefaultAsync(s => s.Key == SettingKey);
        if (row is null) db.Settings.Add(row = new AppSetting { Key = SettingKey });
        row.Value = JsonSerializer.Serialize(this);
        row.UpdatedAt = At;
        await db.SaveChangesAsync();
    }

    /// <summary>Counts of the things people care about, from the live database at backup time.</summary>
    public static async Task<Dictionary<string, int>> CountAsync(AppDbContext db) => new()
    {
        ["stories"] = await db.Memories.CountAsync(m => m.Body != "" || m.Attachments.Any()),
        ["photos and files"] = await db.Attachments.CountAsync(a => a.RemovedAt == null),
        ["people in the tree"] = await db.People.CountAsync(),
        ["people who sign in"] = await db.Users.CountAsync(),
    };

    public static (int Files, long Bytes) Measure(string dir)
    {
        if (!Directory.Exists(dir)) return (0, 0);
        int files = 0; long bytes = 0;
        foreach (var f in new DirectoryInfo(dir).EnumerateFiles("*", SearchOption.AllDirectories)) { files++; bytes += f.Length; }
        return (files, bytes);
    }
}

/// <summary>A backup location described for people, with any password in it hidden.</summary>
public sealed record BackupLocation(string Role, string Kind, string Where, string? OnHost);

public static partial class BackupLocations
{
    public static IReadOnlyList<BackupLocation> Describe(AppOptions o)
    {
        var list = new List<BackupLocation>
        {
            new("On this server", Kind(o.ResticRepository, o.RcloneConfigPath), Mask(o.ResticRepository),
                IsLocal(o.ResticRepository) && o.BackupRootOnHost is { } host && o.ResticRepository.StartsWith("/backups", StringComparison.Ordinal)
                    ? host.TrimEnd('/') + o.ResticRepository["/backups".Length..] : null),
        };
        if (o.OffsiteRepository is { } off)
            list.Add(new("Offsite copy", Kind(off, o.RcloneConfigPath), Mask(off), null));
        if (o.PlainCopy)
            list.Add(new("Plain copy (no restic needed to open it)", "a folder", o.PlainCopyDir,
                o.BackupRootOnHost is { } h && o.PlainCopyDir.StartsWith("/backups", StringComparison.Ordinal) ? h.TrimEnd('/') + o.PlainCopyDir["/backups".Length..] : null));
        return list;
    }

    public static bool IsLocal(string repository) => repository.StartsWith('/') || !repository.Contains(':');

    /// <summary>"a folder", "Backblaze B2", "OneDrive (through rclone)"…</summary>
    public static string Kind(string repository, string? rcloneConfig)
    {
        if (IsLocal(repository)) return "a folder";
        var scheme = repository[..repository.IndexOf(':')].ToLowerInvariant();
        return scheme switch
        {
            "b2" => "Backblaze B2",
            "s3" => "Amazon S3 or compatible storage",
            "sftp" => "another computer (SFTP)",
            "rest" => "a restic server",
            "azure" => "Microsoft Azure storage",
            "gs" => "Google Cloud Storage",
            "swift" => "OpenStack Swift",
            "rclone" => RcloneKind(repository, rcloneConfig),
            _ => scheme,
        };
    }

    private static string RcloneKind(string repository, string? configPath)
    {
        var remote = repository.Split(':') is { Length: >= 2 } parts ? parts[1] : "";
        string? type = null;
        if (configPath is not null && File.Exists(configPath))
        {
            string? section = null;
            foreach (var raw in File.ReadLines(configPath))
            {
                var line = raw.Trim();
                if (line.StartsWith('[') && line.EndsWith(']')) section = line[1..^1];
                else if (section == remote && line.StartsWith("type", StringComparison.Ordinal) && line.Split('=', 2) is [_, var t]) type = t.Trim();
            }
        }
        var name = type switch
        {
            "onedrive" => "OneDrive",
            "drive" => "Google Drive",
            "dropbox" => "Dropbox",
            "box" => "Box",
            "pcloud" => "pCloud",
            "webdav" => "a WebDAV server (e.g. Nextcloud)",
            "s3" => "S3-compatible storage",
            "b2" => "Backblaze B2",
            "sftp" => "another computer (SFTP)",
            null => $"\"{remote}\", which isn't in rclone.conf yet",
            _ => type,
        };
        return $"{name} (through rclone)";
    }

    /// <summary>Hides a password in a URL-style repository (rest:https://user:secret@host/…).</summary>
    public static string Mask(string repository) => UserInfo().Replace(repository, "://$1:•••@");

    [GeneratedRegex(@"://([^:/@]+):[^@/]+@")]
    private static partial Regex UserInfo();
}

/// <summary>
/// Whether two folders are on the same disk, from the container's mount table. Backups on the same disk as the stories
/// don't survive that disk failing.
/// </summary>
public static class DiskCheck
{
    /// <summary>The device ("major:minor") behind <paramref name="path"/> in a /proc/self/mountinfo listing.</summary>
    public static string? DeviceOf(string path, string mountinfo)
    {
        string? best = null; var bestLength = -1;
        foreach (var line in mountinfo.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var f = line.Split(' ');
            if (f.Length < 5) continue;
            var mountPoint = f[4].Replace("\\040", " ");
            var covers = path == mountPoint || mountPoint == "/" || path.StartsWith(mountPoint.TrimEnd('/') + "/", StringComparison.Ordinal);
            if (covers && mountPoint.Length > bestLength) { best = f[2]; bestLength = mountPoint.Length; }
        }
        return best;
    }

    /// <summary>True or false when it can tell (on Linux); null when it can't (e.g. on a Mac while developing).</summary>
    public static bool? SameDisk(string a, string b, string mountinfoPath = "/proc/self/mountinfo")
    {
        if (!File.Exists(mountinfoPath)) return null;
        var info = File.ReadAllText(mountinfoPath);
        var (da, db) = (DeviceOf(a, info), DeviceOf(b, info));
        return da is null || db is null ? null : da == db;
    }
}
