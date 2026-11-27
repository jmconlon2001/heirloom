using System.Globalization;
using FamilyMemories.Auth;
using FamilyMemories.Configuration;
using FamilyMemories.Stories;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace FamilyMemories.Pages.Library;

/// <summary>On this day in the family's history (D-081), with a look at the week ahead.</summary>
public sealed class OnThisDayModel(OnThisDayService onThisDay, LibraryService library, AppOptions options, TimeProvider clock) : PageModel
{
    public DateOnly Day { get; private set; }
    public DateOnly Today { get; private set; }
    public List<OnThisDayEntry> OnTheDay { get; private set; } = [];
    public List<OnThisDayEntry> WeekAhead { get; private set; } = [];

    public async Task OnGetAsync(string? date)
    {
        Today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(clock.GetUtcNow().UtcDateTime, options.TimeZone));
        Day = DateOnly.TryParseExact(date, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : Today;
        var visible = library.Visible(User);
        OnTheDay = await onThisDay.GetAsync(visible, User.IsAdmin(), Day);
        WeekAhead = await onThisDay.GetAsync(visible, User.IsAdmin(), Day.AddDays(1), 6);
    }
}
