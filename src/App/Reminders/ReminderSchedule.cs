using FamilyMemories.Data;

namespace FamilyMemories.Reminders;

/// <summary>
/// When reminders are due (pure functions, unit-tested across daylight-saving changes).
/// Weekly / every two weeks: on the chosen weekday at the chosen local time.
/// Monthly: the first chosen weekday of each month. Times are the family member's local time.
/// </summary>
public static class ReminderSchedule
{
    /// <summary>The most recent scheduled moment at or before <paramref name="nowUtc"/>, in UTC; null when reminders are off.</summary>
    public static DateTime? LatestOccurrenceUtc(ReminderFrequency frequency, DayOfWeek day, TimeOnly time, TimeZoneInfo tz, DateTime nowUtc)
    {
        if (frequency == ReminderFrequency.Off) return null;
        var localNow = TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(nowUtc, DateTimeKind.Utc), tz);

        if (frequency == ReminderFrequency.Monthly)
        {
            for (var monthsBack = 0; monthsBack < 3; monthsBack++)
            {
                var first = new DateTime(localNow.Year, localNow.Month, 1).AddMonths(-monthsBack);
                var date = first.AddDays(((int)day - (int)first.DayOfWeek + 7) % 7);
                var utc = ToUtc(date.Add(time.ToTimeSpan()), tz);
                if (utc <= nowUtc) return utc;
            }
            return null;
        }

        var candidate = localNow.Date.AddDays(-(((int)localNow.DayOfWeek - (int)day + 7) % 7));
        var occurrence = ToUtc(candidate.Add(time.ToTimeSpan()), tz);
        return occurrence <= nowUtc ? occurrence : ToUtc(candidate.AddDays(-7).Add(time.ToTimeSpan()), tz);
    }

    /// <summary>
    /// Due when the latest occurrence hasn't been handled yet and is after the person's start.
    /// Every two weeks: at least 13 days after the last one handled.
    /// </summary>
    public static bool IsDue(ReminderFrequency frequency, DateTime? latestOccurrenceUtc, DateTime? lastHandledUtc, DateTime startedUtc)
    {
        if (latestOccurrenceUtc is not { } occurrence) return false;
        if (occurrence <= startedUtc) return false;
        if (lastHandledUtc is { } last)
        {
            if (occurrence <= last) return false;
            if (frequency == ReminderFrequency.EveryTwoWeeks && occurrence - last < TimeSpan.FromDays(13)) return false;
        }
        return true;
    }

    /// <summary>The next scheduled moment after now (for "Your next reminder: Sunday 10:00 AM").</summary>
    public static DateTime? NextOccurrenceUtc(ReminderFrequency frequency, DayOfWeek day, TimeOnly time, TimeZoneInfo tz, DateTime nowUtc, DateTime? lastHandledUtc)
    {
        for (var days = 1; days <= 70; days++)
        {
            var probe = nowUtc.AddDays(days);
            var occ = LatestOccurrenceUtc(frequency, day, time, tz, probe);
            if (occ is { } o && o > nowUtc && IsDue(frequency, o, lastHandledUtc, DateTime.MinValue)) return o;
        }
        return null;
    }

    /// <summary>Local wall-clock time to UTC. A time skipped by "spring forward" moves to the first valid minute after;
    /// a time that happens twice in the autumn uses the first one.</summary>
    public static DateTime ToUtc(DateTime local, TimeZoneInfo tz)
    {
        local = DateTime.SpecifyKind(local, DateTimeKind.Unspecified);
        if (tz.IsInvalidTime(local))
        {
            var probe = local;
            while (tz.IsInvalidTime(probe)) probe = probe.AddMinutes(1);
            local = probe;
        }
        if (tz.IsAmbiguousTime(local))
        {
            var offset = tz.GetAmbiguousTimeOffsets(local).Max();
            return new DateTimeOffset(local, offset).UtcDateTime;
        }
        return TimeZoneInfo.ConvertTimeToUtc(local, tz);
    }
}
