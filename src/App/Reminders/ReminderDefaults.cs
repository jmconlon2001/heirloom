using System.Text.Json;
using FamilyMemories.Configuration;
using FamilyMemories.Data;
using Microsoft.EntityFrameworkCore;

namespace FamilyMemories.Reminders;

/// <summary>The family-wide reminder settings; each person can override frequency, day, time and time zone.</summary>
public sealed record ReminderDefaults(
    ReminderFrequency Frequency,
    DayOfWeek Day,
    TimeOnly Time,
    string TimeZoneId,
    bool QuietAfterAnswer,
    int NudgeAfter,
    bool DigestEnabled,
    DayOfWeek DigestDay)
{
    public const string SettingKey = "reminders.defaults";

    public static ReminderDefaults Initial(AppOptions o) =>
        new(ReminderFrequency.Weekly, DayOfWeek.Sunday, new TimeOnly(10, 0), o.TimeZoneId, true, 3, true, DayOfWeek.Saturday);

    public static async Task<ReminderDefaults> LoadAsync(AppDbContext db, AppOptions o)
    {
        var row = await db.Settings.AsNoTracking().SingleOrDefaultAsync(s => s.Key == SettingKey);
        return row is null ? Initial(o) : JsonSerializer.Deserialize<ReminderDefaults>(row.Value) ?? Initial(o);
    }

    public async Task SaveAsync(AppDbContext db, DateTime now)
    {
        var row = await db.Settings.SingleOrDefaultAsync(s => s.Key == SettingKey);
        if (row is null) db.Settings.Add(row = new AppSetting { Key = SettingKey });
        row.Value = JsonSerializer.Serialize(this);
        row.UpdatedAt = now;
        await db.SaveChangesAsync();
    }
}

/// <summary>A person's settings with the family defaults filled in.</summary>
public sealed record EffectiveReminders(ReminderFrequency Frequency, DayOfWeek Day, TimeOnly Time, TimeZoneInfo TimeZone, bool Snoozed)
{
    public static EffectiveReminders For(ReminderSettings? s, ReminderDefaults d, DateTime nowUtc) => new(
        s?.Frequency ?? d.Frequency,
        s?.Day ?? d.Day,
        s?.Time ?? d.Time,
        TimeZoneInfo.TryFindSystemTimeZoneById(s?.TimeZoneId ?? d.TimeZoneId, out var tz) ? tz : TimeZoneInfo.Utc,
        s?.SnoozedUntil is { } until && until > nowUtc);
}
