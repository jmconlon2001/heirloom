using System.Net;
using System.Text.RegularExpressions;
using FamilyMemories.Data;
using FamilyMemories.Family;
using FamilyMemories.Media;
using FamilyMemories.Stories;
using FamilyMemories.Tests.Infrastructure;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace FamilyMemories.Tests;

public sealed partial class SharingTests(PostgresFixture postgres) : IAsyncLifetime
{
    private readonly AppFactory app = new(postgres);
    public ValueTask InitializeAsync() => app.InitializeAsync();
    public ValueTask DisposeAsync() => app.DisposeAsync();

    private static string Text(string html) => WebUtility.HtmlDecode(Regex.Replace(html, "<[^>]+>", ""));

    /// <summary>The page body whatever the status (a dead link's page is deliberately a 404).</summary>
    private static async Task<string> Body(HttpClient http, string url) => await (await http.GetAsync(url)).Content.ReadAsStringAsync();

    private sealed record Family(AppUser Rose, AppUser Lily, AppUser Cousin, Browser RoseB, Browser LilyB, Browser CousinB);

    private async Task<Family> FamilyAsync()
    {
        var rose = await app.CreateUserAsync("Rose", $"rose-{Guid.NewGuid():N}@example.test", [Roles.Storyteller, Roles.Family], PromptSetKind.Core);
        var lily = await app.CreateUserAsync("Lily", $"lily-{Guid.NewGuid():N}@example.test", [Roles.Family]);
        var cousin = await app.CreateUserAsync("Cousin Ed", $"ed-{Guid.NewGuid():N}@example.test", [Roles.Family]);
        return new(rose, lily, cousin,
            await Browser.SignedInAsync(app, rose.Email!), await Browser.SignedInAsync(app, lily.Email!), await Browser.SignedInAsync(app, cousin.Email!));
    }

    /// <summary>A finished story by Rose with a photo, at the given visibility.</summary>
    private async Task<(Guid MemoryId, Guid PhotoId)> StoryAsync(AppUser rose, Visibility visibility, string title, params Guid[] sharedWith)
    {
        await using var scope = app.Services.CreateAsyncScope();
        var sp = scope.ServiceProvider;
        var memory = await sp.GetRequiredService<MemoryService>().CreateFreeFormAsync(rose.Id, rose.Id);
        await sp.GetRequiredService<MemoryService>().SaveAsync(memory.Id, new MemoryEdit(title, $"The words of {title}. Farmhouse.", default, "Akron"), rose.Id, VersionKind.Save);
        await using var photo = File.OpenRead(AppFactory.Fixture("photo.jpg"));
        var (a, _) = await sp.GetRequiredService<AttachmentService>().AddFromStreamAsync(memory.Id, "p.jpg", photo, rose.Id);
        await sp.GetRequiredService<SharingService>().SetVisibilityAsync(memory.Id, visibility, sharedWith);
        await sp.GetRequiredService<MemoryService>().SetStatusAsync(memory.Id, MemoryStatus.Complete, rose.Id);
        return (memory.Id, a!.Id);
    }

    [Fact]
    public async Task Visibility_decides_who_can_read_a_story_and_its_photos_everywhere()
    {
        var f = await FamilyAsync();
        var all = await StoryAsync(f.Rose, Visibility.AllFamily, "For everyone");
        var selected = await StoryAsync(f.Rose, Visibility.SelectedPeople, "Just for Lily", f.Lily.Id);
        var mine = await StoryAsync(f.Rose, Visibility.Private, "Only Rose");
        var link = await StoryAsync(f.Rose, Visibility.Link, "Link only");

        async Task Can(Browser b, (Guid M, Guid P) s, bool expected)
        {
            Assert.Equal(expected ? HttpStatusCode.OK : HttpStatusCode.NotFound, (await b.Http.GetAsync($"/stories/{s.M}")).StatusCode);
            Assert.Equal(expected ? HttpStatusCode.OK : HttpStatusCode.NotFound, (await b.Http.GetAsync($"/media/{s.P}/thumb")).StatusCode);
        }

        await Can(f.LilyB, all, true);
        await Can(f.LilyB, selected, true);
        await Can(f.LilyB, mine, false);
        await Can(f.LilyB, link, false);

        await Can(f.CousinB, all, true);
        await Can(f.CousinB, selected, false);
        await Can(f.CousinB, mine, false);

        foreach (var s in new[] { all, selected, mine, link }) await Can(f.RoseB, s, true);

        // The library and search list exactly the same stories.
        var cousinLibrary = Text(await f.CousinB.Http.GetStringAsync("/library"));
        Assert.Contains("For everyone", cousinLibrary);
        Assert.DoesNotContain("Just for Lily", cousinLibrary);
        Assert.DoesNotContain("Only Rose", cousinLibrary);
        var lilySearch = Text(await f.LilyB.Http.GetStringAsync("/library/search?q=farmhouse"));
        Assert.Contains("2 stories found", lilySearch);
        Assert.Equal(HttpStatusCode.NotFound, (await f.CousinB.Http.GetAsync($"/library/photos/{selected.PhotoId}")).StatusCode);
    }

    [Fact]
    public async Task Admins_see_everything_and_their_views_of_private_stories_are_logged()
    {
        var f = await FamilyAsync();
        var mine = await StoryAsync(f.Rose, Visibility.Private, "Only Rose");
        var all = await StoryAsync(f.Rose, Visibility.AllFamily, "For everyone");
        var admin = await Browser.SignedInAsync(app, AppFactory.AdminEmail);

        Assert.Equal(HttpStatusCode.OK, (await admin.Http.GetAsync($"/stories/{mine.MemoryId}")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await admin.Http.GetAsync($"/stories/{all.MemoryId}")).StatusCode);
        Assert.Contains("Only Rose", Text(await admin.Http.GetStringAsync("/library")));

        var views = await app.WithDbAsync(db => db.AuditLog.Where(a => a.Action == "memory.admin-view").Select(a => a.TargetId).ToListAsync());
        Assert.Equal([mine.MemoryId.ToString()], views); // the all-family story isn't logged
    }

    [Fact]
    public async Task Every_visibility_picker_says_admins_can_always_see_stories()
    {
        var f = await FamilyAsync();
        var promptId = await app.WithDbAsync(db => db.Prompts.Where(p => p.Key == "roots-born").Select(p => p.Id).SingleAsync());
        var (_, html, _) = await f.RoseB.GetAsync($"/answer/{promptId}");
        Assert.Contains("Family admins can always see your stories.", WebUtility.HtmlDecode(html));
        Assert.Contains("Only certain people", html);
        Assert.Contains($"value=\"{f.Lily.Id}\"", html); // Lily appears in the picker
    }

    [Fact]
    public async Task Selecting_people_on_the_writing_page_shares_with_exactly_them()
    {
        var f = await FamilyAsync();
        var promptId = await app.WithDbAsync(db => db.Prompts.Where(p => p.Key == "roots-born").Select(p => p.Id).SingleAsync());
        var (path, page, _) = await f.RoseB.GetAsync($"/answer/{promptId}");
        var memoryId = Guid.Parse(path.Split('/')[2]);
        var form = new MultipartFormDataContent
        {
            { new StringContent(HtmlForms.AntiforgeryToken(page)), "__RequestVerificationToken" },
            { new StringContent("Born on the farm."), "Body" },
            { new StringContent("SelectedPeople"), "Visibility" },
            { new StringContent(f.Lily.Id.ToString()), "SharedWith" },
        };
        await f.RoseB.Http.PostAsync($"/stories/{memoryId}/write?handler=Finish", form);

        Assert.Equal(HttpStatusCode.OK, (await f.LilyB.Http.GetAsync($"/stories/{memoryId}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await f.CousinB.Http.GetAsync($"/stories/{memoryId}")).StatusCode);
        Assert.Contains("Shared with: Lily.", Text(await f.RoseB.Http.GetStringAsync($"/stories/{memoryId}")));
    }

    [Fact]
    public async Task Drafts_stay_private_until_finished()
    {
        var f = await FamilyAsync();
        var memory = await app.WithServiceAsync<MemoryService, Memory>(s => s.CreateFreeFormAsync(f.Rose.Id, f.Rose.Id));
        await app.WithServiceAsync<MemoryService, SaveResult>(s => s.SaveAsync(memory.Id, new MemoryEdit("Half done", "Not finished", default, null), f.Rose.Id, VersionKind.Save));
        Assert.Equal(HttpStatusCode.NotFound, (await f.LilyB.Http.GetAsync($"/stories/{memory.Id}")).StatusCode);
        Assert.DoesNotContain("Half done", Text(await f.LilyB.Http.GetStringAsync("/library")));
    }

    [Fact]
    public async Task View_as_shows_what_someone_sees_and_cannot_change_anything()
    {
        var f = await FamilyAsync();
        var selected = await StoryAsync(f.Rose, Visibility.SelectedPeople, "Just for Lily", f.Lily.Id);
        var mine = await StoryAsync(f.Rose, Visibility.Private, "Only Rose");
        var admin = await Browser.SignedInAsync(app, AppFactory.AdminEmail);

        var userPage = await admin.Http.GetStringAsync($"/admin/users/{f.Cousin.Id}");
        var start = await admin.Http.PostFormAsync($"/admin/users/{f.Cousin.Id}?handler=ViewAs", userPage);
        Assert.Equal("/", start.Headers.Location?.OriginalString);

        var home = WebUtility.HtmlDecode(await admin.Http.GetStringAsync("/"));
        Assert.Contains("You're seeing the site as <strong>Cousin Ed</strong>", home);
        Assert.Contains("Hello, Cousin", home);
        Assert.DoesNotContain("Family admin", home);
        Assert.Equal(HttpStatusCode.NotFound, (await admin.Http.GetAsync($"/stories/{selected.MemoryId}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await admin.Http.GetAsync($"/stories/{mine.MemoryId}")).StatusCode);
        Assert.NotEqual(HttpStatusCode.OK, (await admin.Http.GetAsync("/admin/users")).StatusCode);

        // Read-only: posting anything is refused.
        var refused = await admin.Http.PostFormAsync("/stories/new", home);
        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        Assert.False(await app.WithDbAsync(db => db.Memories.AnyAsync(m => m.StorytellerId == f.Cousin.Id)));

        var stop = await admin.Http.PostFormAsync("/view-as/stop", home);
        Assert.Equal($"/admin/users/{f.Cousin.Id}", stop.Headers.Location?.OriginalString);
        Assert.Equal(HttpStatusCode.OK, (await admin.Http.GetAsync($"/stories/{mine.MemoryId}")).StatusCode);
        Assert.True(await app.WithDbAsync(db => db.AuditLog.AnyAsync(a => a.Action == "user.view-as" && a.TargetId == f.Cousin.Id.ToString())));
    }

    [Fact]
    public async Task Special_links_work_without_an_account_and_can_be_protected_expired_and_turned_off()
    {
        var f = await FamilyAsync();
        var (memoryId, photoId) = await StoryAsync(f.Rose, Visibility.Link, "For Aunt Mary");
        var links = await f.RoseB.Http.GetStringAsync($"/stories/{memoryId}/links");
        var created = WebUtility.HtmlDecode(await (await f.RoseB.Http.PostFormAsync($"/stories/{memoryId}/links?handler=Create", links,
            ("Label", "Aunt Mary"), ("Expires", "30"), ("Passcode", "lilac"))).Content.ReadAsStringAsync());
        var url = new Uri(LinkRegex().Match(created).Value);
        Assert.Contains("src=\"data:image/png;base64,", created);

        var stranger = app.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = true, BaseAddress = new Uri("https://localhost") });
        var gate = await stranger.GetStringAsync(url.PathAndQuery);
        Assert.Contains("protected with a passcode", gate);
        Assert.Equal(HttpStatusCode.NotFound, (await stranger.GetAsync($"{url.AbsolutePath}/media/{photoId}/thumb")).StatusCode);

        var wrong = await stranger.PostFormAsync(url.PathAndQuery, gate, ("Passcode", "tulip"));
        Assert.Contains("isn't right", WebUtility.HtmlDecode(await wrong.Content.ReadAsStringAsync()));
        await stranger.PostFormAsync(url.PathAndQuery, gate, ("Passcode", "lilac"));
        var story = WebUtility.HtmlDecode(await stranger.GetStringAsync(url.PathAndQuery));
        Assert.Contains("For Aunt Mary", story);
        Assert.Contains("The words of For Aunt Mary.", story);
        Assert.Equal(HttpStatusCode.OK, (await stranger.GetAsync($"{url.AbsolutePath}/media/{photoId}/thumb")).StatusCode);
        // …but not the app's normal media URL, and not other stories.
        Assert.NotEqual(HttpStatusCode.OK, (await stranger.GetAsync($"/media/{photoId}/thumb")).StatusCode);

        // Expired after 30 days.
        app.Clock.Advance(TimeSpan.FromDays(31));
        Assert.Contains("isn't working", WebUtility.HtmlDecode(await Body(stranger, url.PathAndQuery)));
        app.Clock.Advance(TimeSpan.FromDays(-31));

        // Turned off.
        var shareId = await app.WithDbAsync(db => db.LinkShares.Where(s => s.MemoryId == memoryId).Select(s => s.Id).SingleAsync());
        await f.RoseB.Http.PostFormAsync($"/stories/{memoryId}/links?handler=Revoke", await f.RoseB.Http.GetStringAsync($"/stories/{memoryId}/links"), ("shareId", shareId.ToString()));
        Assert.Contains("isn't working", WebUtility.HtmlDecode(await Body(stranger, url.PathAndQuery)));
        Assert.Equal(1, await app.WithDbAsync(db => db.LinkShares.Where(s => s.Id == shareId).Select(s => s.ViewCount).SingleAsync()));
    }

    [Fact]
    public async Task A_link_stops_working_when_the_story_is_no_longer_shared_by_link()
    {
        var f = await FamilyAsync();
        var (memoryId, _) = await StoryAsync(f.Rose, Visibility.Link, "Link story");
        var token = await app.WithServiceAsync<SharingService, string>(s => s.CreateLinkAsync(memoryId, f.Rose.Id, null, null, null));
        var stranger = new Browser(app);
        Assert.Contains("Link story", await stranger.Http.GetStringAsync($"/s/{token}"));

        await app.WithServiceAsync<SharingService, bool>(async s => { await s.SetVisibilityAsync(memoryId, Visibility.Private, []); return true; });
        Assert.Contains("isn't working", WebUtility.HtmlDecode(await Body(stranger.Http, $"/s/{token}")));
    }

    [Fact]
    public async Task Family_can_heart_leave_notes_and_ask_follow_up_questions()
    {
        var f = await FamilyAsync();
        var (memoryId, _) = await StoryAsync(f.Rose, Visibility.AllFamily, "The farm");
        var page = await f.LilyB.Http.GetStringAsync($"/stories/{memoryId}");

        await f.LilyB.Http.PostFormAsync($"/stories/{memoryId}?handler=Heart", page);
        await f.CousinB.Http.PostFormAsync($"/stories/{memoryId}?handler=Heart", await f.CousinB.Http.GetStringAsync($"/stories/{memoryId}"));
        await f.LilyB.Http.PostFormAsync($"/stories/{memoryId}?handler=Comment", page, ("NewComment", "I love this, Grandma!"));
        await f.LilyB.Http.PostFormAsync($"/stories/{memoryId}?handler=FollowUp", page, ("Question", "What was the dog's name?"));

        var rosePage = Text(await f.RoseB.Http.GetStringAsync($"/stories/{memoryId}"));
        Assert.Contains("Lily and Cousin Ed love this.", rosePage);
        Assert.Contains("I love this, Grandma!", rosePage);

        // Hearts toggle off.
        await f.LilyB.Http.PostFormAsync($"/stories/{memoryId}?handler=Heart", page);
        Assert.Equal(1, await app.WithDbAsync(db => db.Reactions.CountAsync(r => r.MemoryId == memoryId)));

        // The follow-up is first on Rose's list, saying who asked.
        var plan = await app.WithServiceAsync<StoryPlanService, StoryPlan>(s => s.GetAsync(f.Rose.Id));
        Assert.True(plan.Next!.IsOneOff);
        Assert.Equal("What was the dog's name?", plan.Next.Prompt.Question);
        Assert.Contains("Lily asked:", WebUtility.HtmlDecode(await f.RoseB.Http.GetStringAsync("/")));

        // Cousin can't delete Lily's note; Rose (the storyteller) can.
        var commentId = await app.WithDbAsync(db => db.Comments.Where(c => c.MemoryId == memoryId).Select(c => c.Id).SingleAsync());
        await f.CousinB.Http.PostFormAsync($"/stories/{memoryId}?handler=DeleteComment", await f.CousinB.Http.GetStringAsync($"/stories/{memoryId}"), ("commentId", commentId.ToString()));
        Assert.Null(await app.WithDbAsync(db => db.Comments.Where(c => c.Id == commentId).Select(c => c.DeletedAt).SingleAsync()));
        await f.RoseB.Http.PostFormAsync($"/stories/{memoryId}?handler=DeleteComment", await f.RoseB.Http.GetStringAsync($"/stories/{memoryId}"), ("commentId", commentId.ToString()));
        Assert.NotNull(await app.WithDbAsync(db => db.Comments.Where(c => c.Id == commentId).Select(c => c.DeletedAt).SingleAsync()));

        // Reads are recorded once per person (for "3 family members read your story").
        Assert.Equal(2, await app.WithDbAsync(db => db.Reads.CountAsync(r => r.MemoryId == memoryId)));
    }

    [GeneratedRegex(@"https://memories\.example\.test/s/[A-Za-z0-9_\-]+")]
    private static partial Regex LinkRegex();
}
