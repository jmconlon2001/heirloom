using System.Text.RegularExpressions;
using FamilyMemories.Data;
using FamilyMemories.Family;
using FamilyMemories.Media;
using FamilyMemories.Stories;
using FamilyMemories.Tests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;

namespace FamilyMemories.E2E;

/// <summary>Phase 3: a family member reads, reacts and explores; relatives without accounts use a link; admins "view as".</summary>
public sealed class FamilyTests(PostgresFixture postgres, BrowsersFixture browsers) : IAsyncLifetime
{
    private readonly KestrelAppFactory app = new(postgres);

    public ValueTask InitializeAsync() => app.InitializeAsync();
    public ValueTask DisposeAsync() => app.DisposeAsync();

    private async Task<IPage> SignInAsync(string email, IBrowserContext? context = null)
    {
        context ??= await browsers.NewIPadAsync(app.BaseUrl);
        var page = await context.NewPageAsync();
        await page.GotoAsync("/account/login");
        await page.GetByLabel("Your email address").FillAsync(email);
        await page.GetByRole(AriaRole.Button, new() { Name = "Email me a sign-in link" }).ClickAsync();
        await Expect(page.GetByRole(AriaRole.Heading, new() { Name = "Check your email" })).ToBeVisibleAsync();
        await page.GotoAsync(CapturingEmailSender.ExtractMagicLink(app.Email.To(email).Last()).PathAndQuery);
        await page.GetByRole(AriaRole.Button, new() { Name = "Sign me in" }).ClickAsync();
        await Expect(page.GetByRole(AriaRole.Heading, new() { NameRegex = new Regex("^Hello") })).ToBeVisibleAsync();
        return page;
    }

    /// <summary>Rose (storyteller), Lily (granddaughter), a finished story with a photo, and a small tree.</summary>
    private async Task<(AppUser Rose, Guid StoryId, Guid JoeId)> SeedAsync(Visibility visibility = Visibility.AllFamily)
    {
        var rose = await app.CreateUserAsync("Rose Smith", "rose-fam@example.test", [Roles.Storyteller, Roles.Family], PromptSetKind.Core);
        await app.CreateUserAsync("Lily Smith", "lily-fam@example.test", [Roles.Family]);
        await using var scope = app.Services.CreateAsyncScope();
        var sp = scope.ServiceProvider;
        var people = sp.GetRequiredService<PeopleService>();
        var rosePerson = await people.CreateAsync(new PersonInput("Rose", "Smith", "Miller", "Grandma Rose", Sex.Female,
            new FuzzyDate(1931, 6, 12, false, null, null), "Akron, Ohio", true, default, null, null, rose.Id, null));
        var joe = await people.CreateAsync(new PersonInput("Joe", "Smith", null, "Grandpa Joe", Sex.Male,
            new FuzzyDate(1928, null, null, true, null, null), null, false, new FuzzyDate(2004, null, null, false, null, null), null, null, null, null));
        var ann = await people.CreateAsync(new PersonInput("Ann", "Smith", null, null, Sex.Female,
            new FuzzyDate(1956, null, null, false, null, null), null, true, default, null, null, null, null));
        await people.AddPartnerAsync(rosePerson.Id, joe.Id, PartnershipKind.Married, 1952, null);
        await people.AddParentAsync(ann.Id, rosePerson.Id, ParentKind.Biological);
        await people.AddParentAsync(ann.Id, joe.Id, ParentKind.Biological);

        var memories = sp.GetRequiredService<MemoryService>();
        var memory = await memories.CreateFreeFormAsync(rose.Id, rose.Id);
        await memories.SaveAsync(memory.Id, new MemoryEdit("The day I met Joe",
            "It was the county fair, summer of 1950.\n\nHe won a ribbon for his tomatoes and gave it to me.", new FuzzyDate(1950, 7, null, false, null, null), "Summit County Fair"), rose.Id, VersionKind.Save);
        await using (var photo = File.OpenRead(AppFactory.Fixture("photo.jpg")))
        {
            var (a, _) = await sp.GetRequiredService<AttachmentService>().AddFromStreamAsync(memory.Id, "fair.jpg", photo, rose.Id);
            await sp.GetRequiredService<AttachmentService>().SetCaptionAsync(memory.Id, a!.Id, "Joe and me at the fair, 1950");
            await people.TagPhotoAsync(a.Id, joe.Id, true);
        }
        await people.SetMemoryPeopleAsync(memory.Id, [joe.Id, rosePerson.Id]);
        await sp.GetRequiredService<SharingService>().SetVisibilityAsync(memory.Id, visibility, []);
        await memories.SetStatusAsync(memory.Id, MemoryStatus.Complete, rose.Id);
        return (rose, memory.Id, joe.Id);
    }

    [Fact]
    public async Task IPad_family_member_reads_hearts_and_explores_the_library_and_tree()
    {
        var (_, storyId, joeId) = await SeedAsync();
        var page = await SignInAsync("lily-fam@example.test");

        // Home shows the newest story.
        await Expect(page.GetByRole(AriaRole.Heading, new() { Name = "New in the Family Library" })).ToBeVisibleAsync();
        await Accessibility.AssertAccessibleAsync(page, "family member home");

        // The library, then the story.
        await page.GetByRole(AriaRole.Navigation, new() { Name = "Main" }).GetByRole(AriaRole.Link, new() { Name = "Family Library" }).ClickAsync();
        await Expect(page.GetByRole(AriaRole.Heading, new() { Name = "Family Library", Exact = true })).ToBeVisibleAsync();
        await Accessibility.AssertAccessibleAsync(page, "family library");
        await page.GetByRole(AriaRole.Link, new() { NameRegex = new Regex("The day I met Joe") }).First.ClickAsync();
        await Expect(page.GetByRole(AriaRole.Heading, new() { Name = "The day I met Joe" })).ToBeVisibleAsync();
        await Expect(page.GetByText("In this photo: Joe Smith")).ToBeVisibleAsync();

        // ♥ and a note.
        await page.GetByRole(AriaRole.Button, new() { Name = "Love this story" }).ClickAsync();
        await Expect(page.GetByRole(AriaRole.Button, new() { Name = "You love this story" })).ToHaveAttributeAsync("aria-pressed", "true");
        await page.GetByLabel("Write a short note to Rose").FillAsync("I never knew about the tomatoes, Grandma!");
        await page.GetByRole(AriaRole.Button, new() { Name = "Add my note" }).ClickAsync();
        await Expect(page.GetByText("I never knew about the tomatoes, Grandma!")).ToBeVisibleAsync();
        await Accessibility.ScreenshotAsync(page, "ipad-story-read");
        await Accessibility.AssertAccessibleAsync(page, "reading a story with hearts and notes");

        // The large photo viewer.
        await page.GotoAsync("/library/photos");
        // Thumbnails are square tiles (the img height attribute must not stretch them).
        var tile = await page.Locator(".photo-grid img").First.BoundingBoxAsync();
        Assert.NotNull(tile);
        Assert.InRange(tile.Height / tile.Width, 0.95, 1.05);
        await page.Locator(".photo-grid a").First.ClickAsync();
        await Expect(page.GetByText("Joe and me at the fair, 1950")).ToBeVisibleAsync();
        await Accessibility.AssertAccessibleAsync(page, "photo viewer");

        // Timeline and search.
        await page.GotoAsync("/library/timeline");
        await Expect(page.GetByRole(AriaRole.Heading, new() { Name = "The 1950s" })).ToBeVisibleAsync();
        await page.GetByLabel("Search the stories").FillAsync("tomatoes");
        await page.GetByRole(AriaRole.Button, new() { Name = "Search" }).ClickAsync();
        await Expect(page.GetByText("1 story found")).ToBeVisibleAsync();

        // The family tree: diagram plus the same family as a list.
        await page.GotoAsync($"/tree?focus={joeId}");
        await Expect(page.Locator("#family-chart .card").First).ToBeVisibleAsync(new() { Timeout = 15_000 });
        // Cards fade in and the zoom animates; check the settled picture.
        await Expect(page.Locator("#family-chart .card")).ToHaveCountAsync(3, new() { Timeout = 15_000 });
        await page.GetByRole(AriaRole.Button, new() { Name = "Zoom in" }).ClickAsync();
        await page.WaitForTimeoutAsync(1500);
        await Expect(page.Locator("#family-chart").GetByText("Rose Smith")).ToBeVisibleAsync();
        await Expect(page.GetByRole(AriaRole.Heading, new() { Name = "Joe Smith's close family" })).ToBeVisibleAsync();
        await Accessibility.ScreenshotAsync(page, "ipad-tree");
        await Accessibility.AssertAccessibleAsync(page, "family tree");
        await page.GetByRole(AriaRole.Link, new() { Name = "Centre on Ann Smith" }).ClickAsync();
        await Expect(page.GetByRole(AriaRole.Heading, new() { Name = "Ann Smith's close family" })).ToBeVisibleAsync();

        // A person page.
        await page.GotoAsync($"/people/{joeId}");
        await Expect(page.GetByRole(AriaRole.Heading, new() { Name = "Joe Smith" })).ToBeVisibleAsync();
        await Expect(page.GetByText("Known as “Grandpa Joe”")).ToBeVisibleAsync();
        await Expect(page.GetByRole(AriaRole.Heading, new() { Name = "Photos Joe appears in" })).ToBeVisibleAsync();
        await Accessibility.AssertAccessibleAsync(page, "person page");
        await Accessibility.AssertNoHorizontalScrollAsync(page);
        Assert.True(storyId != Guid.Empty);
    }

    [Fact]
    public async Task A_relative_without_an_account_opens_a_special_link_with_a_passcode()
    {
        var (rose, storyId, _) = await SeedAsync(Visibility.Link);
        var token = await app.WithServiceAsync<SharingService, string>(s => s.CreateLinkAsync(storyId, rose.Id, TimeSpan.FromDays(30), "lilac", "Aunt Mary"));

        await using var context = await browsers.NewIPadAsync(app.BaseUrl);
        var page = await context.NewPageAsync();
        await page.GotoAsync($"/s/{token}");
        await Expect(page.GetByText("protected with a passcode")).ToBeVisibleAsync();
        await Accessibility.AssertAccessibleAsync(page, "link passcode page");
        await page.GetByLabel("Passcode").FillAsync("lilac");
        await page.GetByRole(AriaRole.Button, new() { Name = "Show me the story" }).ClickAsync();
        await Expect(page.GetByRole(AriaRole.Heading, new() { Name = "The day I met Joe" })).ToBeVisibleAsync();
        await Expect(page.Locator(".gallery img")).ToHaveCountAsync(1);
        await page.WaitForFunctionAsync("() => { const img = document.querySelector('.gallery img'); return img && img.complete && img.naturalWidth > 0; }");
        await Accessibility.AssertAccessibleAsync(page, "story opened by link");
    }

    [Fact]
    public async Task Admin_views_the_site_as_a_family_member()
    {
        await SeedAsync(Visibility.Private);
        var lilyId = await app.WithDbAsync(db => Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.SingleAsync(db.Users, u => u.Email == "lily-fam@example.test"));
        var page = await SignInAsync(AppFactory.AdminEmail);
        await page.GotoAsync($"/admin/users/{lilyId.Id}");
        await page.GetByRole(AriaRole.Button, new() { Name = "See the site as Lily Smith" }).ClickAsync();
        await Expect(page.GetByText("You're seeing the site as")).ToBeVisibleAsync();
        await Expect(page.GetByRole(AriaRole.Heading, new() { Name = "Hello, Lily" })).ToBeVisibleAsync();
        await page.GotoAsync("/library");
        await Expect(page.GetByText("No stories have been shared yet.")).ToBeVisibleAsync(); // Rose's story is private
        await Accessibility.AssertAccessibleAsync(page, "view-as banner");
        await page.GetByRole(AriaRole.Button, new() { Name = "Stop viewing as Lily Smith" }).ClickAsync();
        await Expect(page).ToHaveURLAsync(new Regex($"/admin/users/{lilyId.Id}$"));
        await page.GotoAsync("/library");
        await Expect(page.GetByText("The day I met Joe").First).ToBeVisibleAsync();
    }
}
