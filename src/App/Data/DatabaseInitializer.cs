using System.Diagnostics;
using FamilyMemories.Configuration;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace FamilyMemories.Data;

/// <summary>
/// Runs once at startup, before the app accepts requests:
/// 1. if migrations are pending on a database that already has data, pg_dump it first;
/// 2. apply migrations;
/// 3. seed roles and the bootstrap admin (idempotent).
/// </summary>
public sealed class DatabaseInitializer(
    AppDbContext db,
    UserManager<AppUser> users,
    RoleManager<AppRole> roles,
    FamilyMemories.Stories.PromptSeeder prompts,
    IServiceProvider services,
    AppOptions options,
    TimeProvider clock,
    ILogger<DatabaseInitializer> log)
{
    public async Task InitializeAsync(CancellationToken ct = default) => await InitializeAsync(skipBackup: false, ct);

    public async Task InitializeAsync(bool skipBackup, CancellationToken ct = default)
    {
        var pending = (await db.Database.GetPendingMigrationsAsync(ct)).ToList();
        if (pending.Count > 0)
        {
            var applied = (await db.Database.GetAppliedMigrationsAsync(ct)).ToList();
            if (applied.Count > 0 && options.PreMigrationBackup && !skipBackup)
            {
                if (options.BackupsConfigured)
                {
                    // Full restic backup (database, media, keys) when backups are set up.
                    var backups = services.GetRequiredService<FamilyMemories.Backup.BackupService>();
                    var result = await backups.BackupWithoutRecordAsync(BackupTrigger.PreMigration, ct);
                    if (!result.Succeeded)
                        throw new InvalidOperationException("Migrations are pending but the safety backup failed: " + result.Message);
                    log.LogInformation("Pre-migration backup saved as snapshot {Snapshot}", result.SnapshotId);
                }
                else
                {
                    await BackupBeforeMigrationAsync(pending, ct);
                }
            }
            log.LogInformation("Applying {Count} database migration(s): {Migrations}", pending.Count, pending);
            await db.Database.MigrateAsync(ct);
        }

        await SeedAsync();
        await prompts.SeedAsync();
    }

    private async Task BackupBeforeMigrationAsync(IReadOnlyList<string> pending, CancellationToken ct)
    {
        Directory.CreateDirectory(options.PreMigrationBackupDir);
        var file = Path.Combine(options.PreMigrationBackupDir,
            $"pre-migration-{clock.GetUtcNow():yyyyMMdd-HHmmss}.dump");
        var cs = new NpgsqlConnectionStringBuilder(options.ConnectionString);

        log.LogInformation("Pending migrations {Migrations}; backing up database to {File} first", pending, file);

        var psi = new ProcessStartInfo(options.PgTool("pg_dump"))
        {
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var arg in new[] { "-Fc", "-h", cs.Host!, "-p", cs.Port.ToString(), "-U", cs.Username!, "-d", cs.Database!, "-f", file })
            psi.ArgumentList.Add(arg);
        psi.Environment["PGPASSWORD"] = cs.Password ?? "";

        Process process;
        try
        {
            process = Process.Start(psi) ?? throw new InvalidOperationException("pg_dump did not start");
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            // Refuse to migrate without a backup. Set PRE_MIGRATION_BACKUP=false to override (e.g. local dev without pg_dump).
            throw new InvalidOperationException(
                "Database migrations are pending but pg_dump could not be run to take a safety backup. " +
                "Install the PostgreSQL client, or set PRE_MIGRATION_BACKUP=false to skip.", ex);
        }

        var stderr = await process.StandardError.ReadToEndAsync(ct);
        await process.WaitForExitAsync(ct);
        if (process.ExitCode != 0)
            throw new InvalidOperationException($"Pre-migration pg_dump failed (exit {process.ExitCode}): {stderr}");

        log.LogInformation("Pre-migration backup written to {File}", file);
    }

    private async Task SeedAsync()
    {
        foreach (var role in Roles.All)
        {
            if (!await roles.RoleExistsAsync(role))
                await roles.CreateAsync(new AppRole(role));
        }

        if (options.AdminEmail is null) return;

        var admin = await users.FindByEmailAsync(options.AdminEmail);
        if (admin is null)
        {
            admin = new AppUser
            {
                UserName = options.AdminEmail,
                Email = options.AdminEmail,
                EmailConfirmed = true,
                DisplayName = options.AdminName,
                CreatedAt = clock.GetUtcNow().UtcDateTime,
            };
            var result = await users.CreateAsync(admin);
            if (!result.Succeeded)
                throw new InvalidOperationException("Could not create admin user: " +
                    string.Join("; ", result.Errors.Select(e => e.Description)));
            log.LogInformation("Created bootstrap admin {Email}", options.AdminEmail);
        }

        if (!await users.IsInRoleAsync(admin, Roles.Admin))
            await users.AddToRoleAsync(admin, Roles.Admin);
    }
}
