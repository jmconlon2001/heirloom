using System.Text.Json;
using System.Text.RegularExpressions;
using FamilyMemories.Configuration;
using FamilyMemories.Data;
using Microsoft.EntityFrameworkCore;

namespace FamilyMemories.Admin;

/// <summary>
/// What the site shows, changeable by an admin without editing .env: its name, who to call for help, and the text size and
/// contrast a new device starts with. An empty name or help field falls back to APP_NAME, HELP_NAME or HELP_PHONE.
/// Applied to the <see cref="AppOptions"/> singleton at startup and on every save, so pages, emails and login cards all follow.
/// </summary>
public sealed partial record SiteSettings(string? AppName, string? HelpName, string? HelpPhone, int TextSize, bool HighContrast, string? MainTreeUrl = null)
{
    public const string SettingKey = "site.settings";
    public static readonly SiteSettings Empty = new(null, null, null, 0, false);

    public static async Task<SiteSettings> LoadAsync(AppDbContext db)
    {
        var row = await db.Settings.AsNoTracking().SingleOrDefaultAsync(s => s.Key == SettingKey);
        return row is null ? Empty : JsonSerializer.Deserialize<SiteSettings>(row.Value) ?? Empty;
    }

    public async Task SaveAsync(AppDbContext db, AuditLog audit, DateTime now)
    {
        var row = await db.Settings.SingleOrDefaultAsync(s => s.Key == SettingKey);
        if (row is null) db.Settings.Add(row = new AppSetting { Key = SettingKey });
        row.Value = JsonSerializer.Serialize(this);
        row.UpdatedAt = now;
        audit.Add("site.settings", $"Changed the site settings: name \"{AppName ?? "(from .env)"}\", help {HelpName ?? "(from .env)"} {HelpPhone ?? ""}".TrimEnd());
        await db.SaveChangesAsync();
    }

    public void ApplyTo(AppOptions o)
    {
        o.AppName = AppName ?? o.EnvAppName;
        o.HelpName = HelpName ?? o.EnvHelpName;
        o.HelpPhone = HelpPhone ?? o.EnvHelpPhone;
        o.DefaultTextSize = Math.Clamp(TextSize, 0, 2);
        o.DefaultHighContrast = HighContrast;
        o.MainTreeUrl = MainTreeUrl;
    }

    /// <summary>Digits with the usual punctuation: "(555) 010-0199", "+44 20 7946 0958", "555.010.0199 ext. 12".</summary>
    public static bool IsPhoneNumber(string text) => PhonePattern().IsMatch(text) && text.Count(char.IsDigit) is >= 3 and <= 20;

    [GeneratedRegex(@"^\+?[0-9 ().\-]+(\s*(ext\.?|x)\s*[0-9]+)?$", RegexOptions.IgnoreCase)]
    private static partial Regex PhonePattern();
}
