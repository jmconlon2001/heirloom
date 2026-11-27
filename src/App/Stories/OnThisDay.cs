using FamilyMemories.Data;
using Microsoft.EntityFrameworkCore;

namespace FamilyMemories.Stories;

public enum OnThisDayKind { Story, Birthday, Remembrance, Wedding, Archive, Photo }

/// <summary>Something that happened on this day (month and day) in an earlier year.</summary>
public sealed record OnThisDayEntry(OnThisDayKind Kind, DateOnly Date, bool ExactYear, string Title, string Detail, string Url, Guid? ThumbId)
{
    /// <summary>"75 years ago today", "100 years ago on Thursday", or "" when only the month and day are known.</summary>
    public string YearsAgo(DateOnly today)
    {
        var years = today.Year - Date.Year;
        if (!ExactYear || years <= 0) return "";
        var when = Date.Month == today.Month && Date.Day == today.Day ? "today"
            : "on " + new DateTime(today.Year, Date.Month, Math.Min(Date.Day, DateTime.DaysInMonth(today.Year, Date.Month))).ToString("dddd", System.Globalization.CultureInfo.GetCultureInfo("en-US"));
        return years == 1 ? $"A year ago {when}" : $"{years} years ago {when}";
    }
}

/// <summary>
/// "On this day" (D-081): stories, family birthdays and remembrance days, weddings, archive items and photos whose date
/// falls on this month and day in an earlier year. Only things with a known month and day count ("June 1952", not "1952").
/// Privacy (§7): a living person's birthday is shown only to admins; everyone sees the days of those who have died.
/// </summary>
public sealed class OnThisDayService(AppDbContext db)
{
    /// <param name="visible">The stories this person may read (from StoryAccess/LibraryService).</param>
    /// <param name="from">First day of the window (inclusive).</param>
    /// <param name="days">How many days the window covers (1 = just that day).</param>
    public async Task<List<OnThisDayEntry>> GetAsync(IQueryable<Memory> visible, bool isAdmin, DateOnly from, int days = 1)
    {
        var window = Enumerable.Range(0, Math.Max(1, days)).Select(i => from.AddDays(i)).ToList();
        var monthDays = window.Select(d => (d.Month, d.Day)).ToHashSet();
        // February 29 is remembered on the 28th in other years.
        if (window.Any(d => d.Month == 2 && d.Day == 28 && !DateTime.IsLeapYear(d.Year))) monthDays.Add((2, 29));
        var months = monthDays.Select(m => m.Month).Distinct().ToList();
        bool Matches(int? month, int? day) => month is { } m && day is { } d && monthDays.Contains((m, d));
        var thisYear = window[^1].Year;
        var entries = new List<OnThisDayEntry>();
        static DateOnly On(int year, int month, int day) => new(year, month, Math.Min(day, DateTime.DaysInMonth(year, month)));

        // Stories
        var stories = await visible.AsNoTracking()
            .Where(m => m.Status == MemoryStatus.Complete && m.WhenMonth != null && m.WhenDay != null && m.WhenYear != null && months.Contains(m.WhenMonth!.Value))
            .Select(m => new
            {
                m.Id, m.Title, m.WhenYear, m.WhenMonth, m.WhenDay, m.WhenCirca, Teller = m.Storyteller!.DisplayName,
                Thumb = m.Attachments.Where(a => a.Kind == AttachmentKind.Photo && a.RemovedAt == null && a.State == ProcessingState.Ready)
                    .OrderBy(a => a.SortOrder).Select(a => (Guid?)a.Id).FirstOrDefault(),
            }).ToListAsync();
        foreach (var s in stories.Where(s => Matches(s.WhenMonth, s.WhenDay) && s.WhenYear < thisYear))
            entries.Add(new(OnThisDayKind.Story, On(s.WhenYear!.Value, s.WhenMonth!.Value, s.WhenDay!.Value), !s.WhenCirca,
                s.Title, $"A story told by {s.Teller}", $"/stories/{s.Id}", s.Thumb));

        // Birthdays and remembrance days
        var people = await db.People.AsNoTracking()
            .Where(p => (p.BirthMonth != null && p.BirthDay != null && months.Contains(p.BirthMonth!.Value))
                     || (p.DeathMonth != null && p.DeathDay != null && months.Contains(p.DeathMonth!.Value)))
            .ToListAsync();
        foreach (var p in people)
        {
            if (Matches(p.BirthMonth, p.BirthDay) && (!p.IsLiving || isAdmin))
            {
                var year = p.BirthYear ?? 0;
                var date = On(year > 0 ? year : 2000, p.BirthMonth!.Value, p.BirthDay!.Value);   // 2000 is a leap year: any month and day fit
                var age = year > 0 ? thisYear - year : 0;
                entries.Add(new(OnThisDayKind.Birthday, date, year > 0 && !p.BirthCirca,
                    p.IsLiving ? $"{p.DisplayName}'s birthday" : $"{p.DisplayName} was born",
                    p.IsLiving ? (age > 0 ? $"Turns {age}" : "Birthday") + " · only admins see living people's birthdays"
                               : year > 0 ? $"Born {p.Birth.Format()}{(p.BirthPlace is null ? "" : $" in {p.BirthPlace}")}" : "Born on this day",
                    $"/people/{p.Id}", p.PhotoAttachmentId));
            }
            if (!p.IsLiving && Matches(p.DeathMonth, p.DeathDay) && p.DeathYear is { } died && died < thisYear)
                entries.Add(new(OnThisDayKind.Remembrance, On(died, p.DeathMonth!.Value, p.DeathDay!.Value), !p.DeathCirca,
                    $"Remembering {p.DisplayName}", $"Died {p.Death.Format()}{(p.DeathPlace is null ? "" : $" in {p.DeathPlace}")}",
                    $"/people/{p.Id}", p.PhotoAttachmentId));
        }

        // Weddings (from the main tree's marriage events; each couple once)
        var weddings = await db.PersonFacts.AsNoTracking().Include(f => f.Person)
            .Where(f => f.Kind == "MARR" && f.Month != null && f.Day != null && f.Year != null && months.Contains(f.Month!.Value))
            .ToListAsync();
        foreach (var f in weddings.Where(f => Matches(f.Month, f.Day) && f.Year < thisYear)
                     .GroupBy(f => (f.Year, f.Month, f.Day, Couple: f.PartnerId is { } partnerId
                         ? string.Join("+", new[] { f.PersonId, partnerId }.Order())
                         : string.Join("+", new[] { f.Person!.DisplayName, (f.Value ?? "").Replace("with ", "") }.Order())))
                     .Select(g => g.First()))
        {
            var partner = (f.Value ?? "").StartsWith("with ") ? f.Value![5..] : null;
            entries.Add(new(OnThisDayKind.Wedding, On(f.Year!.Value, f.Month!.Value, f.Day!.Value), !f.Circa,
                partner is null ? $"{f.Person!.DisplayName} was married" : $"{f.Person!.DisplayName} and {partner} were married",
                f.Place is null ? "Wedding anniversary" : $"Married in {f.Place}", $"/people/{f.PersonId}", f.Person.PhotoAttachmentId));
        }

        // The family archive
        var items = await db.ArchiveItems.AsNoTracking()
            .Where(i => i.WhenMonth != null && i.WhenDay != null && i.WhenYear != null && months.Contains(i.WhenMonth!.Value))
            .Select(i => new
            {
                i.Id, i.Title, i.WhenYear, i.WhenMonth, i.WhenDay, i.WhenCirca, i.Place,
                Thumb = i.Files.Where(a => a.RemovedAt == null && a.State == ProcessingState.Ready && a.Width != null).OrderBy(a => a.SortOrder).Select(a => (Guid?)a.Id).FirstOrDefault(),
            }).ToListAsync();
        foreach (var i in items.Where(i => Matches(i.WhenMonth, i.WhenDay) && i.WhenYear < thisYear))
            entries.Add(new(OnThisDayKind.Archive, On(i.WhenYear!.Value, i.WhenMonth!.Value, i.WhenDay!.Value), !i.WhenCirca,
                i.Title, i.Place is null ? "From the family archive" : $"From the family archive · {i.Place}", $"/library/archive/item/{i.Id}", i.Thumb));

        // Photos dated by the camera (story photos this person can see, and archive photos not already listed above)
        var visibleIds = visible.Select(m => m.Id);
        var listed = items.Select(i => i.Id).ToHashSet();
        var photos = await db.Attachments.AsNoTracking()
            .Where(a => a.Kind == AttachmentKind.Photo && a.RemovedAt == null && a.State == ProcessingState.Ready && a.TakenAt != null
                     && (a.ArchiveItemId != null || (a.MemoryId != null && visibleIds.Contains(a.MemoryId.Value))))
            .Select(a => new { a.Id, a.TakenAt, a.Caption, a.MemoryId, a.ArchiveItemId, StoryTitle = a.Memory != null ? a.Memory.Title : null })
            .ToListAsync();
        foreach (var a in photos.Where(a => Matches(a.TakenAt!.Value.Month, a.TakenAt.Value.Day) && a.TakenAt.Value.Year < thisYear
                                         && (a.ArchiveItemId is null || !listed.Contains(a.ArchiveItemId.Value))).Take(24))
            entries.Add(new(OnThisDayKind.Photo, DateOnly.FromDateTime(a.TakenAt!.Value), true,
                a.Caption ?? (a.StoryTitle is { } t ? $"A photo from \"{t}\"" : "A photo"), "Photo taken on this day",
                a.MemoryId is { } mid ? $"/library/photos/{a.Id}" : $"/library/archive/item/{a.ArchiveItemId}", a.Id));

        // In calendar order through the window (it may cross New Year), then by kind, oldest first.
        int Position(OnThisDayEntry e)
        {
            var i = window.FindIndex(d => d.Month == e.Date.Month && d.Day == e.Date.Day);
            return i >= 0 ? i : window.FindIndex(d => d.Month == 2 && d.Day == 28);
        }
        return [.. entries.OrderBy(Position).ThenBy(e => e.Kind).ThenBy(e => e.Date.Year)];
    }
}
