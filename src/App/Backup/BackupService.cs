using System.Text.Json;
using FamilyMemories.Admin;
using FamilyMemories.Configuration;
using FamilyMemories.Data;
using FamilyMemories.Media;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace FamilyMemories.Backup;

public sealed record Snapshot(string Id, string ShortId, DateTime Time, IReadOnlyList<string> Tags);

public sealed record BackupResult(bool Succeeded, string? SnapshotId, string? OffsiteSnapshotId, string Message);

/// <summary>
/// Backups (BUILD_PROMPT §10): pg_dump → restic (database dump, media, Data Protection keys, config) →
/// retention, to the local repository and optionally an offsite one. Also weekly verification and full restore.
/// Only one backup/verify/restore runs at a time.
/// </summary>
public sealed class BackupService(
    AppOptions options,
    IServiceScopeFactory scopes,
    MaintenanceMode maintenance,
    ILogger<BackupService> log,
    TimeProvider clock)
{
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static readonly TimeSpan LongTimeout = TimeSpan.FromHours(6);
    public const string DumpFileName = "database.dump";

    private string DumpPath => Path.Combine(options.BackupStagingDir, DumpFileName);

    /// <summary>What goes into every snapshot. Logs, exports and half-finished uploads are left out.</summary>
    private IEnumerable<string> BackupPaths => [DumpPath, options.MediaDir, options.KeysDir, options.ConfigDir];

    // ------------------------------------------------------------------ backup

    /// <summary>Runs a full backup and records it. Returns the result even on failure (and alerts admins).</summary>
    public async Task<BackupResult> BackupAsync(BackupTrigger trigger, Guid? startedBy = null, CancellationToken ct = default)
    {
        if (!options.BackupsConfigured) return new(false, null, null, "Backups are not set up: RESTIC_PASSWORD is empty.");
        await Gate.WaitAsync(ct);
        try
        {
            var run = await StartRunAsync(BackupKind.Backup, trigger, startedBy);
            var result = await BackupCoreAsync(trigger, ct);
            await FinishRunAsync(run, result.Succeeded, result.Message, result.SnapshotId, result.OffsiteSnapshotId);
            if (!result.Succeeded) await AlertAsync("Backup failed", result.Message);
            return result;
        }
        finally
        {
            Gate.Release();
        }
    }

    /// <summary>Backup without touching the database's own tables (used before migrations, when they may not exist yet).</summary>
    public async Task<BackupResult> BackupWithoutRecordAsync(BackupTrigger trigger, CancellationToken ct = default)
    {
        await Gate.WaitAsync(ct);
        try { return await BackupCoreAsync(trigger, ct); }
        finally { Gate.Release(); }
    }

    private async Task<BackupResult> BackupCoreAsync(BackupTrigger trigger, CancellationToken ct)
    {
        try
        {
            foreach (var dir in new[] { options.MediaDir, options.KeysDir, options.ConfigDir, options.BackupStagingDir })
                Directory.CreateDirectory(dir);

            await DumpDatabaseAsync(DumpPath, ct);
            var tag = trigger.ToString().ToLowerInvariant();

            var primary = await BackupToAsync(options.ResticRepository, options.ResticPassword!, tag, ct);
            string? offsite = null;
            var notes = new List<string> { $"Saved to {options.ResticRepository}" };
            if (options.OffsiteRepository is not null)
            {
                try
                {
                    offsite = await BackupToAsync(options.OffsiteRepository, options.OffsitePassword!, tag, ct);
                    notes.Add($"and to {options.OffsiteRepository}");
                }
                catch (Exception ex) when (ex is InvalidOperationException or TimeoutException)
                {
                    // The local copy is fine; still report the offsite failure loudly.
                    return new(false, primary, null, $"Local backup succeeded ({primary}) but the offsite copy failed: {ex.Message}");
                }
            }
            return new(true, primary, offsite, string.Join(" ", notes) + ".");
        }
        catch (Exception ex) when (ex is InvalidOperationException or TimeoutException or IOException or System.ComponentModel.Win32Exception)
        {
            log.LogError(ex, "Backup failed");
            return new(false, null, null, ex.Message);
        }
        finally
        {
            TryDelete(DumpPath);
        }
    }

    private async Task<string> BackupToAsync(string repository, string password, string tag, CancellationToken ct)
    {
        await EnsureRepositoryAsync(repository, password, ct);
        var result = await ResticAsync(repository, password,
            ["backup", "--json", "--host", "family-memories", "--tag", tag, .. BackupPaths], ct);
        Require(result, "restic backup");

        string? snapshotId = null;
        foreach (var line in result.StdOut.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            if (!line.Contains("\"summary\"")) continue;
            using var doc = JsonDocument.Parse(line);
            if (doc.RootElement.TryGetProperty("snapshot_id", out var id)) snapshotId = id.GetString();
        }
        if (snapshotId is null) throw new InvalidOperationException("restic did not report a snapshot id");

        var forget = await ResticAsync(repository, password,
            ["forget", "--host", "family-memories", "--prune",
             "--keep-daily", options.KeepDaily.ToString(), "--keep-weekly", options.KeepWeekly.ToString(),
             "--keep-monthly", options.KeepMonthly.ToString()], ct);
        Require(forget, "restic forget --prune");
        return snapshotId;
    }

    /// <summary>restic's exit code for "there is no repository here yet".</summary>
    private const int ResticRepositoryMissing = 10;

    private async Task EnsureRepositoryAsync(string repository, string password, CancellationToken ct)
    {
        // restic retries some backend errors for a long time; don't let a bad path hang a backup.
        using var probeTimeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        probeTimeout.CancelAfter(TimeSpan.FromMinutes(3));
        ToolResult probe;
        try
        {
            probe = await ResticAsync(repository, password, ["cat", "config"], probeTimeout.Token);
        }
        catch (TimeoutException)
        {
            throw new InvalidOperationException($"The backup location {repository} did not answer. Is the disk mounted?");
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new InvalidOperationException($"The backup location {repository} did not answer. Is the disk mounted?");
        }
        if (probe.Succeeded) return;
        if (probe.ExitCode != ResticRepositoryMissing)
            throw new InvalidOperationException($"Could not open the backup repository {repository}: {probe.StdErr.Trim()}");

        if (!repository.Contains(':')) Directory.CreateDirectory(repository); // local path
        log.LogInformation("Initialising restic repository {Repository}", repository);
        Require(await ResticAsync(repository, password, ["init"], ct), "restic init");
    }

    private async Task DumpDatabaseAsync(string path, CancellationToken ct)
    {
        var cs = new NpgsqlConnectionStringBuilder(options.ConnectionString);
        // The Hangfire queue is left out: it is rebuilt automatically and must not be rewound by a restore.
        var result = await PgAsync("pg_dump",
            ["-Fc", "--no-owner", "--no-privileges", "--exclude-schema=hangfire", .. ConnectionArgs(cs), "-d", cs.Database!, "-f", path], cs, ct);
        Require(result, "pg_dump");
    }

    // ------------------------------------------------------------------ snapshots

    public async Task<List<Snapshot>> ListSnapshotsAsync(CancellationToken ct = default)
    {
        if (!options.BackupsConfigured) return [];
        var result = await ResticAsync(options.ResticRepository, options.ResticPassword!, ["snapshots", "--json", "--host", "family-memories"], ct);
        if (!result.Succeeded) return [];
        using var doc = JsonDocument.Parse(result.StdOut);
        return [.. doc.RootElement.EnumerateArray().Select(s => new Snapshot(
            s.GetProperty("id").GetString()!,
            s.GetProperty("short_id").GetString()!,
            s.GetProperty("time").GetDateTime().ToUniversalTime(),
            s.TryGetProperty("tags", out var tags) ? [.. tags.EnumerateArray().Select(t => t.GetString()!)] : []))
            .OrderByDescending(s => s.Time)];
    }

    // ------------------------------------------------------------------ verify

    /// <summary>
    /// Weekly: reads back 5% of the stored data, then restores the latest database dump into a
    /// throwaway database and checks that the important tables are there and not empty.
    /// </summary>
    public async Task<BackupResult> VerifyAsync(BackupTrigger trigger, Guid? startedBy = null, CancellationToken ct = default)
    {
        if (!options.BackupsConfigured) return new(false, null, null, "Backups are not set up.");
        await Gate.WaitAsync(ct);
        BackupRun? run = null;
        var scratch = Path.Combine(options.BackupStagingDir, "verify-" + Guid.NewGuid().ToString("N")[..8]);
        var throwaway = "fm_verify_" + Guid.NewGuid().ToString("N")[..10];
        var cs = new NpgsqlConnectionStringBuilder(options.ConnectionString);
        try
        {
            run = await StartRunAsync(BackupKind.Verify, trigger, startedBy);
            Require(await ResticAsync(options.ResticRepository, options.ResticPassword!, ["check", "--read-data-subset=5%"], ct), "restic check");

            var snapshot = (await ListSnapshotsAsync(ct)).FirstOrDefault()
                ?? throw new InvalidOperationException("There are no backups to check yet.");
            Directory.CreateDirectory(scratch);
            var dump = await RestoreDumpAsync(snapshot.Id, scratch, ct);

            await ExecuteAdminSqlAsync($"CREATE DATABASE \"{throwaway}\"", ct);
            var target = new NpgsqlConnectionStringBuilder(cs.ConnectionString) { Database = throwaway };
            Require(await PgAsync("pg_restore", ["--no-owner", "--no-privileges", .. ConnectionArgs(target), "-d", throwaway, dump], target, ct), "pg_restore (test restore)");

            var counts = await CountRowsAsync(target.ConnectionString, ct);
            if (counts.GetValueOrDefault("AspNetUsers") < 1) throw new InvalidOperationException("The test restore has no users.");
            if (counts.GetValueOrDefault("Prompts") < 1) throw new InvalidOperationException("The test restore has no questions.");

            var message = $"Snapshot {snapshot.ShortId} from {snapshot.Time:u} checked; test restore has " +
                          string.Join(", ", counts.Select(c => $"{c.Value} {c.Key}")) + ".";
            await FinishRunAsync(run, true, message, snapshot.Id, null);
            return new(true, snapshot.Id, null, message);
        }
        catch (Exception ex) when (ex is InvalidOperationException or TimeoutException or IOException or NpgsqlException)
        {
            log.LogError(ex, "Backup verification failed");
            if (run is not null) await FinishRunAsync(run, false, ex.Message, null, null);
            await AlertAsync("Backup check failed", ex.Message);
            return new(false, null, null, ex.Message);
        }
        finally
        {
            try { await ExecuteAdminSqlAsync($"DROP DATABASE IF EXISTS \"{throwaway}\" WITH (FORCE)", CancellationToken.None); }
            catch (NpgsqlException ex) { log.LogWarning(ex, "Could not drop {Database}", throwaway); }
            TryDeleteDirectory(scratch);
            Gate.Release();
        }
    }

    // ------------------------------------------------------------------ restore

    /// <summary>
    /// Puts the site in maintenance, takes a safety backup of the current state, restores the database and
    /// files from <paramref name="snapshotId"/>, re-runs migrations, and opens the site again.
    /// </summary>
    public async Task<BackupResult> RestoreAsync(string snapshotId, Guid? startedBy = null, CancellationToken ct = default)
    {
        if (!options.BackupsConfigured) return new(false, null, null, "Backups are not set up.");
        await Gate.WaitAsync(ct);
        maintenance.Enter("We're restoring the family's stories from a backup. This usually takes a few minutes.");
        var scratch = Path.Combine(options.BackupStagingDir, "restore-" + Guid.NewGuid().ToString("N")[..8]);
        string? safety = null;
        try
        {
            var safetyResult = await BackupCoreAsync(BackupTrigger.PreRestore, ct);
            if (!safetyResult.Succeeded)
                throw new InvalidOperationException("The safety backup of the current state failed, so nothing was restored: " + safetyResult.Message);
            safety = safetyResult.SnapshotId;

            Directory.CreateDirectory(scratch);
            Require(await ResticAsync(options.ResticRepository, options.ResticPassword!,
                ["restore", snapshotId, "--target", scratch, .. BackupPaths.SelectMany(p => new[] { "--include", p })], ct), "restic restore");

            var dump = Staged(scratch, DumpPath);
            if (!File.Exists(dump)) throw new InvalidOperationException("That backup doesn't contain a database dump.");
            await RestoreDatabaseAsync(dump, ct);
            SwapDirectory(Staged(scratch, options.MediaDir), options.MediaDir);
            SwapDirectory(Staged(scratch, options.ConfigDir), options.ConfigDir);
            MergeKeys(Staged(scratch, options.KeysDir), options.KeysDir);

            await using (var scope = scopes.CreateAsyncScope())
            {
                await scope.ServiceProvider.GetRequiredService<DatabaseInitializer>().InitializeAsync(skipBackup: true, ct);
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                db.BackupRuns.Add(new BackupRun
                {
                    Id = Guid.CreateVersion7(), Kind = BackupKind.Restore, Trigger = BackupTrigger.Manual,
                    Outcome = BackupOutcome.Succeeded, StartedAt = Now, FinishedAt = Now, SnapshotId = snapshotId,
                    StartedById = startedBy, Message = $"Restored snapshot {Short(snapshotId)}. A safety backup of the state before the restore is snapshot {Short(safety)}.",
                });
                db.AuditLog.Add(new AuditEntry
                {
                    At = Now, ActorId = startedBy, ActorName = "admin", Action = "backup.restore",
                    TargetType = "snapshot", TargetId = snapshotId, Summary = $"Restored backup {Short(snapshotId)} (safety backup {Short(safety)})",
                });
                await db.SaveChangesAsync(ct);
            }
            return new(true, snapshotId, null, $"Restored. The state before the restore was saved as snapshot {Short(safety)}.");
        }
        catch (Exception ex) when (ex is InvalidOperationException or TimeoutException or IOException or NpgsqlException or UnauthorizedAccessException)
        {
            log.LogError(ex, "Restore of {Snapshot} failed", snapshotId);
            var message = ex.Message + (safety is null ? "" : $" The state before the restore is safe in snapshot {Short(safety)}.");
            await AlertAsync("Restore failed", message);
            return new(false, null, null, message);
        }
        finally
        {
            TryDeleteDirectory(scratch);
            maintenance.Exit();
            Gate.Release();
        }
    }

    private async Task RestoreDatabaseAsync(string dump, CancellationToken ct)
    {
        var cs = new NpgsqlConnectionStringBuilder(options.ConnectionString);
        NpgsqlConnection.ClearAllPools();
        // Start from an empty public schema so tables added by newer migrations don't linger.
        await using (var conn = new NpgsqlConnection(cs.ConnectionString))
        {
            await conn.OpenAsync(ct);
            await using var cmd = new NpgsqlCommand("DROP SCHEMA public CASCADE; CREATE SCHEMA public;", conn);
            await cmd.ExecuteNonQueryAsync(ct);
        }
        Require(await PgAsync("pg_restore", ["--no-owner", "--no-privileges", "--exit-on-error", .. ConnectionArgs(cs), "-d", cs.Database!, dump], cs, ct), "pg_restore");
        NpgsqlConnection.ClearAllPools();
    }

    private async Task<string> RestoreDumpAsync(string snapshotId, string scratch, CancellationToken ct)
    {
        Require(await ResticAsync(options.ResticRepository, options.ResticPassword!,
            ["restore", snapshotId, "--target", scratch, "--include", DumpPath], ct), "restic restore (database dump)");
        var dump = Staged(scratch, DumpPath);
        return File.Exists(dump) ? dump : throw new InvalidOperationException("The backup has no database dump.");
    }

    /// <summary>restic restores absolute paths underneath the target folder.</summary>
    private static string Staged(string scratch, string absolute) => Path.Combine(scratch, absolute.TrimStart('/', '\\'));

    private static void SwapDirectory(string restored, string live)
    {
        var old = live + ".before-restore";
        TryDeleteDirectory(old);
        if (Directory.Exists(live)) Directory.Move(live, old);
        if (Directory.Exists(restored)) Directory.Move(restored, live);
        else Directory.CreateDirectory(live);
        TryDeleteDirectory(old); // the safety snapshot already holds it
    }

    /// <summary>Keys are merged, not replaced, so both old cookies and the current session keep working.</summary>
    private static void MergeKeys(string restored, string live)
    {
        if (!Directory.Exists(restored)) return;
        Directory.CreateDirectory(live);
        foreach (var file in Directory.GetFiles(restored))
        {
            var target = Path.Combine(live, Path.GetFileName(file));
            if (!File.Exists(target)) File.Copy(file, target);
        }
    }

    // ------------------------------------------------------------------ helpers

    private async Task<Dictionary<string, long>> CountRowsAsync(string connectionString, CancellationToken ct)
    {
        var counts = new Dictionary<string, long>();
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync(ct);
        foreach (var table in new[] { "AspNetUsers", "Prompts", "Memories", "Attachments", "MemoryVersions" })
        {
            await using var cmd = new NpgsqlCommand($"SELECT count(*) FROM \"{table}\"", conn);
            counts[table] = (long)(await cmd.ExecuteScalarAsync(ct))!;
        }
        NpgsqlConnection.ClearPool(conn);
        return counts;
    }

    private async Task ExecuteAdminSqlAsync(string sql, CancellationToken ct)
    {
        var cs = new NpgsqlConnectionStringBuilder(options.ConnectionString) { Database = "postgres", Pooling = false };
        await using var conn = new NpgsqlConnection(cs.ConnectionString);
        await conn.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(sql, conn);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static string[] ConnectionArgs(NpgsqlConnectionStringBuilder cs) =>
        ["-h", cs.Host!, "-p", cs.Port.ToString(), "-U", cs.Username!];

    private Task<ToolResult> PgAsync(string tool, IEnumerable<string> args, NpgsqlConnectionStringBuilder cs, CancellationToken ct) =>
        ExternalTool.RunAsync(options.PgTool(tool), args, LongTimeout, ct, new Dictionary<string, string> { ["PGPASSWORD"] = cs.Password ?? "" });

    private Task<ToolResult> ResticAsync(string repository, string password, IEnumerable<string> args, CancellationToken ct) =>
        ExternalTool.RunAsync(options.ResticPath, args, LongTimeout, ct, new Dictionary<string, string>
        {
            ["RESTIC_REPOSITORY"] = repository,
            ["RESTIC_PASSWORD"] = password,
            ["RESTIC_CACHE_DIR"] = Path.Combine(options.BackupStagingDir, "restic-cache"),
        });

    private static void Require(ToolResult result, string what)
    {
        if (!result.Succeeded) throw new InvalidOperationException($"{what} failed: {result.StdErr.Trim()}");
    }

    private DateTime Now => clock.GetUtcNow().UtcDateTime;
    private static string Short(string? id) => id is null ? "?" : id[..Math.Min(8, id.Length)];

    private async Task<BackupRun> StartRunAsync(BackupKind kind, BackupTrigger trigger, Guid? startedBy)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var run = new BackupRun { Id = Guid.CreateVersion7(), Kind = kind, Trigger = trigger, StartedAt = Now, StartedById = startedBy };
        db.BackupRuns.Add(run);
        await db.SaveChangesAsync();
        return run;
    }

    private async Task FinishRunAsync(BackupRun run, bool ok, string message, string? snapshot, string? offsite)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await db.BackupRuns.Where(r => r.Id == run.Id).ExecuteUpdateAsync(s => s
            .SetProperty(r => r.Outcome, ok ? BackupOutcome.Succeeded : BackupOutcome.Failed)
            .SetProperty(r => r.FinishedAt, Now)
            .SetProperty(r => r.Message, message.Length > 4000 ? message[..4000] : message)
            .SetProperty(r => r.SnapshotId, snapshot)
            .SetProperty(r => r.OffsiteSnapshotId, offsite));
    }

    private async Task AlertAsync(string subject, string message)
    {
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            await scope.ServiceProvider.GetRequiredService<AdminAlerts>().SendAsync(subject, message);
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Could not send admin alert");
        }
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch (IOException) { }
    }

    private static void TryDeleteDirectory(string path)
    {
        try { if (Directory.Exists(path)) Directory.Delete(path, recursive: true); } catch (IOException) { }
    }
}
