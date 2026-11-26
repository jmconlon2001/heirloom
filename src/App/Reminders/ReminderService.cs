using FamilyMemories.Admin;
using FamilyMemories.Auth;
using FamilyMemories.Configuration;
using FamilyMemories.Data;
using FamilyMemories.Email;
using FamilyMemories.Stories;
using Hangfire;
using Microsoft.EntityFrameworkCore;

namespace FamilyMemories.Reminders;

public sealed record TickResult(int Sent, int Skipped, int Nudges);

/// <summary>
/// Reminder emails (§8). Runs every 15 minutes. For each storyteller whose scheduled moment has
/// arrived: skip if paused/snoozed, finished, waiting for next week's question, or (by default)
/// they wrote something since the last reminder; otherwise email the next question with a one-tap
/// sign-in link. After N reminders without activity, nudge the admins.
/// Also: the daily "family read your story" summary and the weekly "New family stories" digest.
/// </summary>
public sealed class ReminderService(
    AppDbContext db,
    EmailService email,
    MagicLinkService links,
    StoryPlanService plans,
    StoryAccess access,
    AdminAlerts alerts,
    AppOptions options,
    TimeProvider clock,
    ILogger<ReminderService> log)
{
    private DateTime Now => clock.GetUtcNow().UtcDateTime;

    public async Task<TickResult> TickAsync()
    {
        var now = Now;
        var defaults = await ReminderDefaults.LoadAsync(db, options);
        var storytellers = await db.Users
            .Where(u => u.Email != null && (u.LockoutEnd == null || u.LockoutEnd < DateTimeOffset.UtcNow)
                        && (u.PromptSetId != null || db.Prompts.Any(p => p.ForUserId == u.Id && p.IsActive)))
            .ToListAsync();
        int sent = 0, skipped = 0, nudges = 0;

        foreach (var user in storytellers)
        {
            var settings = await db.ReminderSettings.SingleOrDefaultAsync(s => s.UserId == user.Id);
            var eff = EffectiveReminders.For(settings, defaults, now);
            var occurrence = ReminderSchedule.LatestOccurrenceUtc(eff.Frequency, eff.Day, eff.Time, eff.TimeZone, now);
            var started = user.PacingStartedAt ?? user.CreatedAt;
            if (!ReminderSchedule.IsDue(eff.Frequency, occurrence, settings?.LastScheduledAt, started)) continue;

            if (settings is null) db.ReminderSettings.Add(settings = new ReminderSettings { UserId = user.Id });
            // Activity is measured since the previous slot (sent *or* skipped), so writing something
            // quiets exactly one reminder, not every one after it.
            var since = settings.LastScheduledAt ?? settings.LastReminderSentAt ?? started;
            settings.LastScheduledAt = occurrence;

            // Anything written since then? They're active: reset the nudge counter.
            var active = await db.Memories.AnyAsync(m => (m.StorytellerId == user.Id || m.WrittenById == user.Id)
                && m.UpdatedAt > since && m.Status != MemoryStatus.NotStarted);
            if (active)
            {
                settings.RemindersSinceActivity = 0;
                settings.NudgeSentAt = null;
            }

            var plan = await plans.GetAsync(user.Id);
            var next = plan.Next;
            if (eff.Snoozed || plan.IsComplete || next is null || (active && defaults.QuietAfterAnswer && settings.LastReminderSentAt is not null))
            {
                skipped++;
                await db.SaveChangesAsync();
                continue;
            }

            var token = await links.IssueAsync(user, MagicLinkPurpose.Reminder, $"/answer/{next.Prompt.Id}", null);
            var hints = next.Prompt.Hints.Cast<object?>().ToList();
            var remaining = plan.Slots.Count(s => !s.IsDone);
            await email.SendAsync(EmailKind.Reminder, new Recipient(user.Email!, user.DisplayName, user.Id), new Dictionary<string, object?>
            {
                ["question"] = next.Prompt.Question,
                ["prompt_title"] = next.Prompt.Title,
                ["hints"] = hints,
                ["asked_by"] = next.IsOneOff ? next.Prompt.AskedByUser?.DisplayName : null,
                ["link"] = links.BuildUrl(token!).ToString(),
                ["progress"] = plan.SharedCount switch
                {
                    0 => "Every story you share is saved for the whole family.",
                    1 => "You've shared 1 story so far. Thank you.",
                    var n => $"You've shared {n} stories so far. Thank you.",
                } + (remaining > 1 ? $" {remaining - 1} more questions after this one, whenever you're ready." : ""),
            });
            if (next.IsOneOff && next.Prompt.FollowUpOfMemoryId is not null)
                await db.Prompts.Where(p => p.Id == next.Prompt.Id).ExecuteUpdateAsync(s => s.SetProperty(p => p.NotifiedAt, now));

            settings.LastReminderSentAt = now;
            settings.RemindersSinceActivity++;
            sent++;

            if (settings.RemindersSinceActivity >= defaults.NudgeAfter && settings.NudgeSentAt is null)
            {
                settings.NudgeSentAt = now;
                nudges++;
                var lastActivity = await db.Memories.Where(m => (m.StorytellerId == user.Id || m.WrittenById == user.Id) && m.Status != MemoryStatus.NotStarted)
                    .MaxAsync(m => (DateTime?)m.UpdatedAt) ?? started;
                await NudgeAdminsAsync(user, settings.RemindersSinceActivity, lastActivity);
            }
            await db.SaveChangesAsync();
        }

        if (sent + nudges > 0) log.LogInformation("Reminders: {Sent} sent, {Skipped} skipped, {Nudges} nudges", sent, skipped, nudges);
        return new TickResult(sent, skipped, nudges);
    }

    private async Task NudgeAdminsAsync(AppUser storyteller, int reminders, DateTime lastActivity)
    {
        var weeks = Math.Max(1, (int)Math.Round((Now - lastActivity).TotalDays / 7));
        foreach (var admin in await alerts.AdminsAsync())
        {
            await email.SendAsync(EmailKind.AdminNudge, admin, new Dictionary<string, object?>
            {
                ["storyteller"] = storyteller.DisplayName,
                ["weeks"] = weeks,
                ["reminders"] = reminders,
                ["link"] = options.BuildUrl($"admin/users/{storyteller.Id}").ToString(),
            });
        }
        await alerts.NtfyAsync("A storyteller has gone quiet", $"{storyteller.DisplayName} hasn't added a story in {weeks} weeks. Maybe give them a call?");
    }

    /// <summary>Daily: "Lily, Ed and Ann read your story “The farm”".</summary>
    public async Task<int> SendReadSummariesAsync()
    {
        var pending = await db.Reads.Where(r => r.NotifiedAt == null)
            .Join(db.Memories, r => r.MemoryId, m => m.Id, (r, m) => new { Read = r, m.Id, m.Title, m.StorytellerId })
            .Where(x => x.Read.UserId != x.StorytellerId)
            .ToListAsync();
        var readerNames = await db.Users.Where(u => pending.Select(p => p.Read.UserId).Contains(u.Id)).ToDictionaryAsync(u => u.Id, u => u.DisplayName);
        var sent = 0;

        foreach (var byTeller in pending.GroupBy(p => p.StorytellerId))
        {
            var teller = await db.Users.Include(u => u.PromptSet).SingleOrDefaultAsync(u => u.Id == byTeller.Key);
            var optedOut = await db.ReminderSettings.AnyAsync(s => s.UserId == byTeller.Key && s.ReadNotificationsOptOut);
            if (teller?.Email is not null && !optedOut)
            {
                var stories = byTeller.GroupBy(p => (p.Id, p.Title)).Select(g => new Dictionary<string, object?>
                {
                    ["title"] = g.Key.Title,
                    ["readers"] = Names([.. g.Select(x => readerNames.GetValueOrDefault(x.Read.UserId, "Someone"))]),
                    ["url"] = options.BuildUrl($"stories/{g.Key.Id}").ToString(),
                    ["count"] = g.Count(),
                }).ToList();
                var total = byTeller.Select(p => p.Read.UserId).Distinct().Count();
                await email.SendAsync(EmailKind.ReadSummary, new Recipient(teller.Email, teller.DisplayName, teller.Id), new Dictionary<string, object?>
                {
                    ["headline"] = stories.Count == 1
                        ? $"{(total == 1 ? "A family member" : $"{total} family members")} read your story “{stories[0]["title"]}”"
                        : $"{total} family members read your stories",
                    ["stories"] = stories.Cast<object?>().ToList(),
                });
                sent++;
            }
            foreach (var p in byTeller) p.Read.NotifiedAt = Now;
        }
        await db.SaveChangesAsync();
        return sent;
    }

    /// <summary>Weekly: the stories finished in the last week that each person may read (not their own).</summary>
    public async Task<int> SendDigestsAsync()
    {
        var defaults = await ReminderDefaults.LoadAsync(db, options);
        if (!defaults.DigestEnabled) return 0;
        var now = Now;
        var users = await db.Users.Where(u => u.Email != null && (u.LockoutEnd == null || u.LockoutEnd < DateTimeOffset.UtcNow)).ToListAsync();
        var sent = 0;
        foreach (var user in users)
        {
            var settings = await db.ReminderSettings.SingleOrDefaultAsync(s => s.UserId == user.Id);
            if (settings?.DigestOptOut == true) continue;
            var since = settings?.LastDigestAt ?? now.AddDays(-7);
            var stories = await access.ReadableByRules(user.Id)
                .Where(m => m.Status == MemoryStatus.Complete && m.CompletedAt > since && m.CompletedAt <= now && m.StorytellerId != user.Id)
                .OrderBy(m => m.CompletedAt)
                .Select(m => new { m.Id, m.Title, Teller = m.Storyteller!.DisplayName })
                .ToListAsync();
            if (settings is null) db.ReminderSettings.Add(settings = new ReminderSettings { UserId = user.Id });
            settings.LastDigestAt = now;
            if (stories.Count == 0) continue;

            await email.SendAsync(EmailKind.Digest, new Recipient(user.Email!, user.DisplayName, user.Id), new Dictionary<string, object?>
            {
                ["stories"] = stories.Select(s => (object?)new Dictionary<string, object?>
                {
                    ["title"] = s.Title, ["teller"] = s.Teller, ["url"] = options.BuildUrl($"stories/{s.Id}").ToString(),
                }).ToList(),
            });
            sent++;
        }
        await db.SaveChangesAsync();
        return sent;
    }

    private static string Names(IReadOnlyList<string> names)
    {
        var distinct = names.Distinct().ToList();
        return distinct.Count switch
        {
            1 => distinct[0],
            2 => $"{distinct[0]} and {distinct[1]}",
            _ => $"{string.Join(", ", distinct.Take(distinct.Count - 1))} and {distinct[^1]}",
        };
    }

    public static void Schedule(IRecurringJobManager recurring, AppOptions options)
    {
        var tz = new RecurringJobOptions { TimeZone = options.TimeZone };
        recurring.AddOrUpdate<ReminderService>("reminders-tick", s => s.TickAsync(), "*/15 * * * *", tz);
        recurring.AddOrUpdate<ReminderService>("read-summaries", s => s.SendReadSummariesAsync(), Cron.Daily(17), tz);
        recurring.AddOrUpdate<ReminderService>("weekly-digest", s => s.SendDigestsAsync(), Cron.Weekly(DayOfWeek.Saturday, 9), tz);
    }
}
