using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using FamilyMemories.Backup;
using FamilyMemories.Configuration;
using FamilyMemories.Data;
using FamilyMemories.Email;
using Hangfire;
using Microsoft.EntityFrameworkCore;

namespace FamilyMemories.Web;

public enum StatusLevel { Ok, Warning, Problem }

/// <summary>One part of the system. <see cref="Name"/> and <see cref="Level"/> are public; <see cref="Summary"/> and <see cref="Data"/> are for admins and monitors only.</summary>
public sealed record ComponentStatus(string Key, string Name, StatusLevel Level, string Summary, IReadOnlyDictionary<string, object?> Data);

public sealed record SystemStatusReport(StatusLevel Level, DateTime CheckedAt, DateTime StartedAt, string Version, bool Maintenance, IReadOnlyList<ComponentStatus> Components)
{
    public static string Word(StatusLevel level) => level switch { StatusLevel.Ok => "ok", StatusLevel.Warning => "warning", _ => "problem" };

    /// <summary>What anyone may see: overall state and when it was checked.</summary>
    public object Public() => new { status = Word(Level), checkedAt = CheckedAt };

    /// <summary>The full report for admins and monitors with STATUS_TOKEN.</summary>
    public object Detailed() => new
    {
        status = Word(Level),
        checkedAt = CheckedAt,
        version = Version,
        startedAt = StartedAt,
        uptimeSeconds = (long)(CheckedAt - StartedAt).TotalSeconds,
        maintenance = Maintenance,
        components = Components.Select(c => new { key = c.Key, name = c.Name, status = Word(c.Level), summary = c.Summary, details = c.Data }),
    };
}

/// <summary>
/// Checks every part of Family Memories for the /status page and /api/status. Results are cached for 15 seconds
/// so an anonymous visitor can't use the endpoint to hammer the database or the PDF service.
/// </summary>
public sealed class SystemStatusService(IServiceScopeFactory scopes, AppOptions options, MaintenanceMode maintenance,
    IHttpClientFactory http, TimeProvider clock, IServiceProvider root)
{
    private static readonly DateTime ProcessStarted = Process.GetCurrentProcess().StartTime.ToUniversalTime();
    private readonly SemaphoreSlim gate = new(1, 1);
    private SystemStatusReport? cached;
    public static readonly TimeSpan CacheFor = TimeSpan.FromSeconds(15);

    public async Task<SystemStatusReport> GetAsync(CancellationToken ct = default)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        if (cached is { } hit && now - hit.CheckedAt < CacheFor && hit.Maintenance == maintenance.IsOn) return hit;
        await gate.WaitAsync(ct);
        try
        {
            now = clock.GetUtcNow().UtcDateTime;
            if (cached is { } again && now - again.CheckedAt < CacheFor && again.Maintenance == maintenance.IsOn) return again;
            return cached = await CheckAsync(now, ct);
        }
        finally { gate.Release(); }
    }

    /// <summary>Forget the cached report (tests, and after an admin fixes something).</summary>
    public void Invalidate() => cached = null;

    /// <summary>True when the request may see the detailed report: a signed-in admin, or a monitor with the STATUS_TOKEN.</summary>
    public bool MaySeeDetails(HttpContext http)
    {
        if (http.User.IsInRole(Roles.Admin)) return true;
        if (options.StatusToken is not { } token) return false;
        var header = http.Request.Headers.Authorization.ToString();
        if (!header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)) return false;
        var given = Encoding.UTF8.GetBytes(header["Bearer ".Length..].Trim());
        return CryptographicOperations.FixedTimeEquals(given, Encoding.UTF8.GetBytes(token));
    }

    private async Task<SystemStatusReport> CheckAsync(DateTime now, CancellationToken ct)
    {
        var components = new List<ComponentStatus>();
        if (maintenance.IsOn)
        {
            // A restore is running: don't touch the database.
            components.Add(new("website", "Website", StatusLevel.Warning, maintenance.Message ?? "Down for maintenance.", Empty));
            return new(StatusLevel.Warning, now, ProcessStarted, AppOptions.Version, true, components);
        }

        components.Add(new("website", "Website", StatusLevel.Ok, "Running.", Empty));
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var database = await Safe("database", "Database", () => Database(db, ct));
        components.Add(database);
        var dbUp = database.Level != StatusLevel.Problem;

        components.Add(await Safe("pdf", "PDF and book maker", () => Gotenberg(ct)));
        if (dbUp)
        {
            components.Add(await Safe("backups", "Backups", () => Backups(scope.ServiceProvider, now)));
            components.Add(await Safe("email", "Email", () => Email(scope.ServiceProvider, db, now, ct)));
        }
        components.Add(await Safe("jobs", "Background tasks", () => Task.FromResult(Jobs())));
        components.Add(await Safe("storage", "Storage space", () => Task.FromResult(Storage())));
        if (options.TranscriptionEnabled)
            components.Add(await Safe("transcription", "Speech-to-text", () => Whisper(ct)));

        var level = components.Max(c => c.Level);
        return new(level, now, ProcessStarted, AppOptions.Version, false, components);
    }

    private static readonly IReadOnlyDictionary<string, object?> Empty = new Dictionary<string, object?>();

    private static async Task<ComponentStatus> Safe(string key, string name, Func<Task<ComponentStatus>> check)
    {
        try { return await check(); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new(key, name, StatusLevel.Problem, $"The check failed: {ex.GetType().Name}: {ex.Message}", Empty);
        }
    }

    private static async Task<ComponentStatus> Database(AppDbContext db, CancellationToken ct)
    {
        var watch = Stopwatch.StartNew();
        if (!await db.Database.CanConnectAsync(ct))
            return new("database", "Database", StatusLevel.Problem, "Can't connect to PostgreSQL.", Empty);
        var ms = watch.ElapsedMilliseconds;
        var size = await db.Database.SqlQueryRaw<long>("SELECT pg_database_size(current_database()) AS \"Value\"").SingleAsync(ct);
        var stories = await db.Memories.CountAsync(ct);
        var people = await db.Users.CountAsync(ct);
        return new("database", "Database", StatusLevel.Ok, $"Connected in {ms} ms. {Size(size)}, {stories} stories, {people} people who sign in.",
            new Dictionary<string, object?> { ["latencyMs"] = ms, ["sizeBytes"] = size, ["stories"] = stories, ["users"] = people });
    }

    private async Task<ComponentStatus> Gotenberg(CancellationToken ct)
    {
        const string key = "pdf", name = "PDF and book maker";
        if (options.GotenbergUrl is null)
            return new(key, name, StatusLevel.Warning, "GOTENBERG_URL isn't set, so PDF and book exports won't work.", Empty);
        var watch = Stopwatch.StartNew();
        try
        {
            using var response = await http.CreateClient("gotenberg").GetAsync(new Uri(new Uri(options.GotenbergUrl.TrimEnd('/') + "/"), "health"), ct);
            return response.IsSuccessStatusCode
                ? new(key, name, StatusLevel.Ok, $"Gotenberg answered in {watch.ElapsedMilliseconds} ms.", new Dictionary<string, object?> { ["latencyMs"] = watch.ElapsedMilliseconds })
                : new(key, name, StatusLevel.Problem, $"Gotenberg answered {(int)response.StatusCode}.", Empty);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return new(key, name, StatusLevel.Problem, "Gotenberg isn't answering, so PDF and book exports won't work.", Empty);
        }
    }

    private static async Task<ComponentStatus> Backups(IServiceProvider services, DateTime now)
    {
        const string key = "backups", name = "Backups";
        var h = await services.GetRequiredService<BackupStatusQuery>().GetAsync();
        var data = new Dictionary<string, object?>
        {
            ["configured"] = h.Configured,
            ["lastGoodBackup"] = h.LastGoodBackup?.FinishedAt,
            ["lastGoodCheck"] = h.LastGoodVerify?.FinishedAt,
            ["running"] = h.Running,
        };
        if (!h.Configured) return new(key, name, StatusLevel.Warning, "Not set up: RESTIC_PASSWORD is empty, so nothing is backed up.", data);
        if (h.LastBackup?.Outcome == BackupOutcome.Failed) return new(key, name, StatusLevel.Problem, $"The last backup failed: {h.LastBackup.Message}", data);
        if (h.LastVerify?.Outcome == BackupOutcome.Failed) return new(key, name, StatusLevel.Problem, $"The last backup check failed: {h.LastVerify.Message}", data);
        if (h.LastGoodBackup?.FinishedAt is not { } last) return new(key, name, StatusLevel.Warning, "No backup has finished yet.", data);
        var age = now - last;
        var summary = $"Last good backup {Ago(age)}" + (h.LastGoodVerify?.FinishedAt is { } v ? $"; last check {Ago(now - v)}." : "; not checked yet.");
        return new(key, name, h.Healthy(now) ? StatusLevel.Ok : StatusLevel.Problem, h.Healthy(now) ? summary : "No good backup in the last 36 hours. " + summary, data);
    }

    private static async Task<ComponentStatus> Email(IServiceProvider services, AppDbContext db, DateTime now, CancellationToken ct)
    {
        const string key = "email", name = "Email";
        var smtp = await services.GetRequiredService<SmtpSettingsStore>().GetAsync();
        var since = now.AddHours(-24);
        var failed = await db.EmailLog.CountAsync(e => e.Status == EmailStatus.Failed && e.CreatedAt >= since, ct);
        var sent = await db.EmailLog.CountAsync(e => e.Status == EmailStatus.Sent && e.SentAt >= since, ct);
        var lastSent = await db.EmailLog.Where(e => e.Status == EmailStatus.Sent).MaxAsync(e => (DateTime?)e.SentAt, ct);
        var lastFailed = await db.EmailLog.Where(e => e.Status == EmailStatus.Failed).MaxAsync(e => (DateTime?)e.CreatedAt, ct);
        var data = new Dictionary<string, object?> { ["configured"] = smtp.IsConfigured, ["sent24h"] = sent, ["failed24h"] = failed, ["lastSent"] = lastSent };
        if (!smtp.IsConfigured) return new(key, name, StatusLevel.Problem, "No mail server is set up, so nobody can get a sign-in link.", data);
        var summary = $"{sent} sent and {failed} failed in the last 24 hours" + (lastSent is { } ls ? $"; last sent {Ago(now - ls)}." : ".");
        // Failures that haven't been followed by a success mean email is broken right now.
        if (failed > 0 && (lastSent is null || lastFailed > lastSent)) return new(key, name, StatusLevel.Problem, "The most recent emails failed. " + summary, data);
        return new(key, name, failed > 0 ? StatusLevel.Warning : StatusLevel.Ok, summary, data);
    }

    private ComponentStatus Jobs()
    {
        const string key = "jobs", name = "Background tasks";
        if (!options.JobsEnabled) return new(key, name, StatusLevel.Ok, "Running inline (JOBS_ENABLED=false).", Empty);
        var storage = root.GetService<JobStorage>();
        if (storage is null) return new(key, name, StatusLevel.Problem, "The job scheduler isn't running.", Empty);
        var s = storage.GetMonitoringApi().GetStatistics();
        var data = new Dictionary<string, object?> { ["servers"] = s.Servers, ["enqueued"] = s.Enqueued, ["processing"] = s.Processing, ["scheduled"] = s.Scheduled, ["failed"] = s.Failed };
        var summary = $"{s.Servers} worker(s), {s.Processing} running, {s.Enqueued} waiting, {s.Failed} failed.";
        if (s.Servers == 0) return new(key, name, StatusLevel.Problem, "No worker is running, so reminders, backups and exports won't happen. " + summary, data);
        return new(key, name, s.Failed > 0 ? StatusLevel.Warning : StatusLevel.Ok, s.Failed > 0 ? summary + " See Background jobs." : summary, data);
    }

    private ComponentStatus Storage()
    {
        const string key = "storage", name = "Storage space";
        var places = new List<(string Label, string Path)> { ("data", options.DataDir) };
        var repo = options.ResticRepository;
        if (Path.IsPathRooted(repo))
        {
            var existing = repo;
            while (!Directory.Exists(existing) && Path.GetDirectoryName(existing) is { } parent) existing = parent;
            if (Directory.Exists(existing)) places.Add(("backups", existing));
        }
        var level = StatusLevel.Ok;
        var parts = new List<string>();
        var data = new Dictionary<string, object?>();
        foreach (var (label, path) in places)
        {
            if (!Directory.Exists(path)) continue;
            var drive = new DriveInfo(Path.GetFullPath(path));
            var free = drive.AvailableFreeSpace;
            var total = drive.TotalSize;
            var pct = total > 0 ? free * 100.0 / total : 100;
            data[label] = new { freeBytes = free, totalBytes = total, freePercent = Math.Round(pct, 1) };
            parts.Add($"{label}: {Size(free)} free of {Size(total)}");
            var l = free < 1L << 30 || pct < 2 ? StatusLevel.Problem : free < 5L << 30 || pct < 10 ? StatusLevel.Warning : StatusLevel.Ok;
            if (l > level) level = l;
        }
        var summary = parts.Count == 0 ? "Couldn't measure." : string.Join("; ", parts) + ".";
        return new(key, name, level, level == StatusLevel.Ok ? summary : "Running low on space. " + summary, data);
    }

    private async Task<ComponentStatus> Whisper(CancellationToken ct)
    {
        const string key = "transcription", name = "Speech-to-text";
        if (options.WhisperUrl is null) return new(key, name, StatusLevel.Warning, "FEATURE_TRANSCRIPTION is on but WHISPER_URL isn't set.", Empty);
        try
        {
            using var response = await http.CreateClient("gotenberg").GetAsync(options.WhisperUrl, ct);
            return (int)response.StatusCode < 500
                ? new(key, name, StatusLevel.Ok, "The Whisper service is answering.", Empty)
                : new(key, name, StatusLevel.Problem, $"Whisper answered {(int)response.StatusCode}.", Empty);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return new(key, name, StatusLevel.Problem, "The Whisper service isn't answering, so recordings won't be written down.", Empty);
        }
    }

    public static string Size(long bytes) => bytes switch
    {
        >= 1L << 40 => $"{bytes / (double)(1L << 40):0.#} TB",
        >= 1L << 30 => $"{bytes / (double)(1L << 30):0.#} GB",
        >= 1L << 20 => $"{bytes / (double)(1L << 20):0.#} MB",
        _ => $"{bytes / 1024.0:0} KB",
    };

    public static string Ago(TimeSpan d) => d.TotalMinutes < 2 ? "just now"
        : d.TotalHours < 1 ? $"{(int)d.TotalMinutes} minutes ago"
        : d.TotalDays < 1 ? $"{(int)d.TotalHours} hours ago"
        : d.TotalDays < 2 ? "yesterday" : $"{(int)d.TotalDays} days ago";
}
