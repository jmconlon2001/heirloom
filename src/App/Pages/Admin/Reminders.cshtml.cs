using FamilyMemories.Admin;
using FamilyMemories.Configuration;
using FamilyMemories.Data;
using FamilyMemories.Reminders;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace FamilyMemories.Pages.Admin;

public sealed class RemindersModel(AppDbContext db, AppOptions options, AuditLog audit, TimeProvider clock) : PageModel
{
    public static readonly string[] CommonTimeZones =
    [
        "America/New_York", "America/Chicago", "America/Denver", "America/Phoenix", "America/Los_Angeles", "America/Anchorage",
        "Pacific/Honolulu", "America/Toronto", "America/Vancouver", "Europe/London", "Europe/Dublin", "Europe/Paris",
        "Europe/Berlin", "Australia/Sydney", "Pacific/Auckland", "UTC",
    ];

    [BindProperty] public ReminderFrequency Frequency { get; set; }
    [BindProperty] public DayOfWeek Day { get; set; }
    [BindProperty] public TimeOnly Time { get; set; }
    [BindProperty] public string TimeZoneId { get; set; } = "";
    [BindProperty] public bool QuietAfterAnswer { get; set; }
    [BindProperty] public int NudgeAfter { get; set; }
    [BindProperty] public bool DigestEnabled { get; set; }
    public IEnumerable<string> TimeZones => CommonTimeZones.Contains(TimeZoneId) ? CommonTimeZones : [TimeZoneId, .. CommonTimeZones];

    public async Task OnGetAsync()
    {
        var d = await ReminderDefaults.LoadAsync(db, options);
        (Frequency, Day, Time, TimeZoneId, QuietAfterAnswer, NudgeAfter, DigestEnabled) =
            (d.Frequency, d.Day, d.Time, d.TimeZoneId, d.QuietAfterAnswer, d.NudgeAfter, d.DigestEnabled);
    }

    public async Task<IActionResult> OnPostAsync()
    {
        var current = await ReminderDefaults.LoadAsync(db, options);
        var tz = TimeZoneInfo.TryFindSystemTimeZoneById(TimeZoneId, out _) ? TimeZoneId : current.TimeZoneId;
        await (current with
        {
            Frequency = Frequency, Day = Day, Time = Time, TimeZoneId = tz, QuietAfterAnswer = QuietAfterAnswer,
            NudgeAfter = Math.Clamp(NudgeAfter, 1, 20), DigestEnabled = DigestEnabled,
        }).SaveAsync(db, clock.GetUtcNow().UtcDateTime);
        await audit.WriteAsync("reminders.defaults", $"Changed the reminder schedule to {Frequency} on {Day} at {Time:HH\\:mm} ({tz})");
        TempData["Flash"] = "Schedule saved.";
        return LocalRedirect("/admin/reminders");
    }
}
