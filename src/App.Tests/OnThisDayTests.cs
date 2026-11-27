using System.Net;
using FamilyMemories.Data;
using FamilyMemories.Family;
using FamilyMemories.Reminders;
using FamilyMemories.Stories;
using FamilyMemories.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace FamilyMemories.Tests;

public sealed class OnThisDayTests(PostgresFixture postgres) : IAsyncLifetime
{
    private readonly AppFactory app = new(postgres);

    public async ValueTask InitializeAsync()
    {
        app.Clock.Set(new DateTimeOffset(2026, 6, 12, 15, 0, 0, TimeSpan.Zero));   // Friday, June 12, 11:00 in New York
        await app.InitializeAsync();
    }

    public ValueTask DisposeAsync() => app.DisposeAsync();

    private sealed record Family(AppUser Rose, AppUser Lily, Person Joe, Person Ann, Guid FairStory, Guid PrivateStory);

    private async Task<Family> SeedAsync()
    {
        var rose = await app.CreateUserAsync("Rose Smith", "otd-rose@example.test", [Roles.Storyteller, Roles.Family], PromptSetKind.Core);
        var lily = await app.CreateUserAsync("Lily Smith", "otd-lily@example.test", [Roles.Family]);
        await using var scope = app.Services.CreateAsyncScope();
        var sp = scope.ServiceProvider;
        var people = sp.GetRequiredService<PeopleService>();
        var memories = sp.GetRequiredService<MemoryService>();
        var db = sp.GetRequiredService<AppDbContext>();

        var joe = await people.CreateAsync(new PersonInput("Joseph", "Smith", null, null, Sex.Male, new FuzzyDate(1928, 6, 12, false, null, null), "Akron",
            false, new FuzzyDate(2004, 6, 15, false, null, null), "Akron", null, null, null));
        var ann = await people.CreateAsync(new PersonInput("Ann", "Smith", null, null, Sex.Female, new FuzzyDate(1956, 6, 12, false, null, null), null,
            true, default, null, null, null, null));
        var rosePerson = await people.CreateAsync(new PersonInput("Rose", "Smith", null, null, Sex.Female, default, null, true, default, null, null, rose.Id, null));
        // Married June 12, 1952: the main tree keeps it on both partners.
        foreach (var (p, partner) in new[] { (joe, "Rose Smith"), (rosePerson, "Joseph Smith") })
            db.PersonFacts.Add(new PersonFact { Id = Guid.NewGuid(), PersonId = p.Id, Kind = "MARR", Label = "Marriage", Year = 1952, Month = 6, Day = 12, Place = "St. Vincent's", Value = $"with {partner}", FromGedcom = true });
        // A living person's birth date from the main tree.
        db.PersonFacts.Add(new PersonFact { Id = Guid.NewGuid(), PersonId = ann.Id, Kind = "BIRT", Label = "Birth", Year = 1956, Month = 6, Day = 12, FromGedcom = true });
        db.ArchiveItems.Add(new ArchiveItem { Id = Guid.NewGuid(), Title = "Letter from Joe in France", WhenYear = 1944, WhenMonth = 6, WhenDay = 12, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow });
        await db.SaveChangesAsync();

        async Task<Guid> Story(string title, FuzzyDate when, Visibility visibility)
        {
            var m = await memories.CreateFreeFormAsync(rose.Id, rose.Id);
            await memories.SaveAsync(m.Id, new MemoryEdit(title, "Words.", when, null), rose.Id, VersionKind.Save);
            await memories.SetStatusAsync(m.Id, MemoryStatus.Complete, rose.Id);
            await sp.GetRequiredService<SharingService>().SetVisibilityAsync(m.Id, visibility, []);
            return m.Id;
        }
        var fair = await Story("The county fair", new FuzzyDate(1950, 6, 12, false, null, null), Visibility.AllFamily);
        var secret = await Story("A private memory", new FuzzyDate(1960, 6, 12, false, null, null), Visibility.Private);
        await Story("Only a year", new FuzzyDate(1955, null, null, false, null, null), Visibility.AllFamily);   // no day: never "on this day"
        return new Family(rose, lily, joe, ann, fair, secret);
    }

    [Fact]
    public async Task The_family_sees_stories_weddings_and_the_departed_and_admins_also_see_living_birthdays()
    {
        var f = await SeedAsync();
        var lily = await Browser.SignedInAsync(app, "otd-lily@example.test");
        var page = WebUtility.HtmlDecode(await lily.Http.GetStringAsync("/library/on-this-day"));
        Assert.Contains("Friday, June 12 in the family's history.", page);
        Assert.Contains("76 years ago today", page);
        Assert.Contains("The county fair", page);
        Assert.Contains("Joseph Smith was born", page);
        Assert.Contains("Born June 12, 1928 in Akron", page);
        Assert.Equal(1, CountOf(page, "were married"));               // once for the couple, not once per partner
        Assert.Contains("Letter from Joe in France", page);
        Assert.DoesNotContain("A private memory", page);              // Rose's private story
        Assert.DoesNotContain("Ann Smith's birthday", page);          // a living person's birthday: admins only (§7)
        Assert.DoesNotContain("Only a year", page);
        var ahead = page[page.IndexOf("Coming up this week")..];
        Assert.Contains("Remembering Joseph Smith", ahead);           // died June 15
        Assert.Contains("22 years ago on Monday", ahead);

        var home = WebUtility.HtmlDecode(await lily.Http.GetStringAsync("/"));
        Assert.Contains("On this day", home);
        Assert.Contains("See all", home);

        var admin = await Browser.SignedInAsync(app, AppFactory.AdminEmail);
        var adminPage = WebUtility.HtmlDecode(await admin.Http.GetStringAsync("/library/on-this-day"));
        Assert.Contains("Ann Smith's birthday", adminPage);
        Assert.Contains("Turns 70", adminPage);

        // Other days, and a day with nothing on it.
        var other = WebUtility.HtmlDecode(await lily.Http.GetStringAsync("/library/on-this-day?date=2026-06-15"));
        Assert.Contains("Remembering Joseph Smith", other);
        var empty = WebUtility.HtmlDecode(await lily.Http.GetStringAsync("/library/on-this-day?date=2026-01-02"));
        Assert.Contains("Nothing we know of happened on January 2 yet", empty);
        Assert.True(f.PrivateStory != Guid.Empty);
    }

    [Fact]
    public async Task A_living_persons_birth_date_from_the_main_tree_shows_only_its_year_to_the_family()
    {
        var f = await SeedAsync();
        var lily = await Browser.SignedInAsync(app, "otd-lily@example.test");
        var page = WebUtility.HtmlDecode(await lily.Http.GetStringAsync($"/people/{f.Ann.Id}"));
        Assert.DoesNotContain("June 12, 1956", page);
        Assert.Contains("<span class=\"event-date\">1956</span>", page);
        var admin = await Browser.SignedInAsync(app, AppFactory.AdminEmail);
        Assert.Contains("June 12, 1956", WebUtility.HtmlDecode(await admin.Http.GetStringAsync($"/people/{f.Ann.Id}")));
    }

    [Fact]
    public async Task February_29_is_remembered_on_the_28th_in_other_years()
    {
        await app.WithServiceAsync<PeopleService, Person>(s => s.CreateAsync(new PersonInput("Leap", "Day", null, null, Sex.Unknown,
            new FuzzyDate(1940, 2, 29, false, null, null), null, false, new FuzzyDate(2000, null, null, false, null, null), null, null, null, null)));
        await using var scope = app.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var entries = await scope.ServiceProvider.GetRequiredService<OnThisDayService>().GetAsync(db.Memories, isAdmin: false, new DateOnly(2027, 2, 28));
        Assert.Contains(entries, e => e.Title == "Leap Day was born");
    }

    [Fact]
    public async Task The_weekly_email_lists_whats_coming_up_in_the_familys_history()
    {
        await SeedAsync();
        await app.WithServiceAsync<ReminderService, int>(s => s.SendDigestsAsync());   // stories completed "today" are in it
        var digest = app.Email.To("otd-lily@example.test").Last();
        Assert.Contains("Coming up this week in the family's history", digest.TextBody);
        Assert.Contains("Monday: Remembering Joseph Smith (22 years)", digest.TextBody);
        Assert.DoesNotContain("Ann Smith's birthday", digest.TextBody);
    }

    private static int CountOf(string text, string part) => (text.Length - text.Replace(part, "").Length) / part.Length;
}
