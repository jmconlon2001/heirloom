using System.Text.Json;
using FamilyMemories.Configuration;
using FamilyMemories.Data;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;

namespace FamilyMemories.Email;

/// <summary>
/// SMTP settings: what an admin saved in the app (password encrypted with Data Protection),
/// otherwise the values from .env. Cached; saving clears the cache.
/// </summary>
public sealed class SmtpSettingsStore(IServiceScopeFactory scopes, AppOptions options, IDataProtectionProvider protection)
{
    public const string SettingKey = "smtp";
    private readonly IDataProtector protector = protection.CreateProtector("smtp-settings");
    private SmtpOptions? cached;

    private sealed record Stored(string Host, int Port, SmtpSecurity Security, string? Username, string? ProtectedPassword, string FromAddress, string FromName);

    public async Task<SmtpOptions> GetAsync()
    {
        if (cached is not null) return cached;
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var row = await db.Settings.AsNoTracking().SingleOrDefaultAsync(s => s.Key == SettingKey);
        if (row is null) return cached = options.Smtp;

        var s = JsonSerializer.Deserialize<Stored>(row.Value)!;
        string? password = null;
        if (s.ProtectedPassword is not null)
        {
            try { password = protector.Unprotect(s.ProtectedPassword); }
            catch (System.Security.Cryptography.CryptographicException) { password = null; }
        }
        return cached = new SmtpOptions
        {
            Host = s.Host, Port = s.Port, Security = s.Security, Username = s.Username,
            Password = password, FromAddress = s.FromAddress, FromName = s.FromName,
        };
    }

    public bool IsFromAdminUi { get; private set; }

    /// <param name="password">Null keeps the saved password.</param>
    public async Task SaveAsync(SmtpOptions settings, string? password, DateTime now)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var current = await GetAsync();
        var keep = password is null ? current.Password : password.Length == 0 ? null : password;
        var stored = new Stored(settings.Host.Trim(), settings.Port, settings.Security, string.IsNullOrWhiteSpace(settings.Username) ? null : settings.Username.Trim(),
            keep is null ? null : protector.Protect(keep), settings.FromAddress.Trim(), settings.FromName.Trim());
        var row = await db.Settings.SingleOrDefaultAsync(s => s.Key == SettingKey);
        if (row is null) db.Settings.Add(row = new AppSetting { Key = SettingKey });
        row.Value = JsonSerializer.Serialize(stored);
        row.UpdatedAt = now;
        await db.SaveChangesAsync();
        cached = null;
    }

    public async Task<bool> HasSavedSettingsAsync()
    {
        await using var scope = scopes.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<AppDbContext>().Settings.AnyAsync(s => s.Key == SettingKey);
    }
}
