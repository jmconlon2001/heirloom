using FamilyMemories.Admin;
using FamilyMemories.Configuration;
using FamilyMemories.Data;
using FamilyMemories.Email;
using FamilyMemories.Reminders;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace FamilyMemories.Pages.EmailPrefs;

/// <summary>
/// Reached from the link at the bottom of every email; no sign-in needed (the link is signed).
/// Every change is a button press (POST), so mail scanners that open links can't change anything.
/// </summary>
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed class PreferencesModel(AppDbContext db, EmailLinks links, AppOptions options, AuditLog audit, TimeProvider clock) : PageModel
{
    public AppUser? Person { get; private set; }
    public bool IsStoryteller { get; private set; }
    public bool Paused { get; private set; }
    public string? SnoozedUntil { get; private set; }
    public string? NextReminder { get; private set; }
    public bool Digest { get; private set; }
    public bool Reads { get; private set; }

    private ReminderSettings? settings;

    private async Task<bool> LoadAsync(string token)
    {
        Response.Headers["X-Robots-Tag"] = "noindex";
        if (links.UserFromToken(token) is not { } id) return false;
        Person = await db.Users.SingleOrDefaultAsync(u => u.Id == id);
        if (Person is null) return false;
        IsStoryteller = Person.PromptSetId is not null || await db.Prompts.AnyAsync(p => p.ForUserId == id && p.IsActive);
        settings = await db.ReminderSettings.SingleOrDefaultAsync(s => s.UserId == id);

        var now = clock.GetUtcNow().UtcDateTime;
        var defaults = await ReminderDefaults.LoadAsync(db, options);
        var eff = EffectiveReminders.For(settings, defaults, now);
        Paused = eff.Frequency == ReminderFrequency.Off;
        if (settings?.SnoozedUntil is { } until && until > now)
            SnoozedUntil = TimeZoneInfo.ConvertTimeFromUtc(until, eff.TimeZone).ToString("dddd, MMMM d");
        if (ReminderSchedule.NextOccurrenceUtc(eff.Frequency, eff.Day, eff.Time, eff.TimeZone, now, settings?.LastScheduledAt) is { } next)
            NextReminder = TimeZoneInfo.ConvertTimeFromUtc(next, eff.TimeZone).ToString("dddd, MMMM d 'at' h:mm tt");
        Digest = settings?.DigestOptOut != true;
        Reads = settings?.ReadNotificationsOptOut != true;
        return true;
    }

    private ReminderSettings Settings()
    {
        if (settings is null) db.ReminderSettings.Add(settings = new ReminderSettings { UserId = Person!.Id });
        return settings;
    }

    public async Task OnGetAsync(string token) => await LoadAsync(token);

    public async Task<IActionResult> OnPostSnoozeAsync(string token)
    {
        if (!await LoadAsync(token)) return Page();
        Settings().SnoozedUntil = clock.GetUtcNow().UtcDateTime.AddDays(14);
        audit.Add("reminders.snooze", $"{Person!.DisplayName} snoozed reminders for 2 weeks", "user", Person.Id);
        await db.SaveChangesAsync();
        TempData["Flash"] = "Done. We'll send the next question in two weeks.";
        return LocalRedirect($"/email/preferences/{token}");
    }

    public async Task<IActionResult> OnPostPauseAsync(string token)
    {
        if (!await LoadAsync(token)) return Page();
        Settings().Frequency = ReminderFrequency.Off;
        audit.Add("reminders.off", $"{Person!.DisplayName} turned reminder emails off", "user", Person.Id);
        await db.SaveChangesAsync();
        TempData["Flash"] = "Reminder emails are turned off. You can still answer questions any time on the website.";
        return LocalRedirect($"/email/preferences/{token}");
    }

    public async Task<IActionResult> OnPostResumeAsync(string token)
    {
        if (!await LoadAsync(token)) return Page();
        var s = Settings();
        s.Frequency = null; // back to the family's default
        s.SnoozedUntil = null;
        s.LastScheduledAt = clock.GetUtcNow().UtcDateTime; // don't fire a stale one straight away
        audit.Add("reminders.on", $"{Person!.DisplayName} turned reminder emails back on", "user", Person.Id);
        await db.SaveChangesAsync();
        TempData["Flash"] = "Reminder emails are on again.";
        return LocalRedirect($"/email/preferences/{token}");
    }

    public async Task<IActionResult> OnPostOtherAsync(string token, bool digest, bool reads)
    {
        if (!await LoadAsync(token)) return Page();
        var s = Settings();
        s.DigestOptOut = !digest;
        if (IsStoryteller) s.ReadNotificationsOptOut = !reads;
        await db.SaveChangesAsync();
        TempData["Flash"] = "Saved.";
        return LocalRedirect($"/email/preferences/{token}");
    }
}
