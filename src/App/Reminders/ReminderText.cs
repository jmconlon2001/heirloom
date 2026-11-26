using FamilyMemories.Configuration;
using FamilyMemories.Data;

namespace FamilyMemories.Reminders;

/// <summary>"Next question by email: Sunday, December 1 at 10:00 AM", for home and admin pages.</summary>
public static class ReminderText
{
    public static async Task<string> DescribeAsync(AppDbContext db, AppOptions options, ReminderSettings? settings, AppUser person, DateTime nowUtc)
    {
        if (person.PromptSetId is null) return "Not a storyteller, so no question reminders.";
        var defaults = await ReminderDefaults.LoadAsync(db, options);
        var eff = EffectiveReminders.For(settings, defaults, nowUtc);
        if (eff.Frequency == ReminderFrequency.Off) return "Reminder emails are off.";
        string Local(DateTime utc) => TimeZoneInfo.ConvertTimeFromUtc(utc, eff.TimeZone).ToString("dddd, MMMM d 'at' h:mm tt");
        if (eff.Snoozed) return $"Taking a break from reminders until {Local(settings!.SnoozedUntil!.Value)}.";
        var next = ReminderSchedule.NextOccurrenceUtc(eff.Frequency, eff.Day, eff.Time, eff.TimeZone, nowUtc, settings?.LastScheduledAt);
        return next is null ? "No reminder is scheduled." : $"Next question by email: {Local(next.Value)}.";
    }
}
