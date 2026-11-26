using System.Net;
using FamilyMemories.Data;
using FamilyMemories.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace FamilyMemories.Tests;

public sealed class DemoTests(PostgresFixture postgres) : IAsyncLifetime
{
    private sealed class DemoFactory(PostgresFixture postgres, bool demo) : AppFactory(postgres)
    {
        protected override IDictionary<string, string?> Settings => new Dictionary<string, string?> { ["DEMO_MODE"] = demo ? "true" : "false" };
    }

    private readonly DemoFactory app = new(postgres, demo: true);
    public ValueTask InitializeAsync() => app.InitializeAsync();
    public ValueTask DisposeAsync() => app.DisposeAsync();

    [Fact]
    public async Task Demo_mode_seeds_a_sample_family_and_offers_one_tap_sign_in_for_each_role()
    {
        Assert.Equal(6, await app.WithDbAsync(db => db.Memories.CountAsync(m => m.Status == MemoryStatus.Complete)));
        Assert.Equal(7, await app.WithDbAsync(db => db.People.CountAsync()));
        Assert.True(await app.WithDbAsync(db => db.Attachments.AnyAsync(a => a.Kind == AttachmentKind.Photo && a.State == ProcessingState.Ready)));

        var browser = new Browser(app);
        var login = WebUtility.HtmlDecode(await browser.Http.GetStringAsync("/account/login"));
        Assert.Contains("Try the demo", login);
        Assert.Contains("Demo site with a made-up family", login);

        var asLily = await browser.Http.PostFormAsync("/account/login?handler=Demo", login, ("who", "lily"));
        Assert.Equal("/", asLily.Headers.Location?.OriginalString);
        var home = WebUtility.HtmlDecode(await browser.Http.GetStringAsync("/"));
        Assert.Contains("Hello, Lily", home);
        Assert.Contains("Write for Grandma Rose", home);                          // she's Rose's helper
        // Lily is Rose's helper, so she can read even Rose's private story; a cousin can't.
        Assert.Contains("Someone you miss", WebUtility.HtmlDecode(await browser.Http.GetStringAsync("/library")));
        await app.CreateUserAsync("Cousin Ed", "ed-demo@example.test", [Roles.Family]);
        var ed = await Browser.SignedInAsync(app, "ed-demo@example.test");
        var edLibrary = WebUtility.HtmlDecode(await ed.Http.GetStringAsync("/library"));
        Assert.Contains("How you met", edLibrary);
        Assert.DoesNotContain("Someone you miss", edLibrary);

        var rose = new Browser(app);
        await rose.Http.PostFormAsync("/account/login?handler=Demo", await rose.Http.GetStringAsync("/account/login"), ("who", "rose"));
        var roseHome = WebUtility.HtmlDecode(await rose.Http.GetStringAsync("/"));
        Assert.Contains("Lily Smith asked:", roseHome);                         // the follow-up question

        var admin = new Browser(app);
        await admin.Http.PostFormAsync("/account/login?handler=Demo", await admin.Http.GetStringAsync("/account/login"), ("who", "admin"));
        Assert.Contains("Family admin", await admin.Http.GetStringAsync("/"));

        // Seeding again changes nothing.
        await app.WithServiceAsync<FamilyMemories.Demo.DemoSeeder, bool>(async s => { await s.SeedAsync(); return true; });
        Assert.Equal(6, await app.WithDbAsync(db => db.Memories.CountAsync(m => m.Status == MemoryStatus.Complete)));
    }
}

public sealed class NoDemoTests(PostgresFixture postgres) : IAsyncLifetime
{
    private readonly AppFactory app = new(postgres);
    public ValueTask InitializeAsync() => app.InitializeAsync();
    public ValueTask DisposeAsync() => app.DisposeAsync();

    [Fact]
    public async Task Without_demo_mode_there_is_no_shortcut_sign_in()
    {
        var browser = new Browser(app);
        var login = await browser.Http.GetStringAsync("/account/login");
        Assert.DoesNotContain("Try the demo", login);
        var attempt = await browser.Http.PostFormAsync("/account/login?handler=Demo", login, ("who", "admin"));
        Assert.Equal(HttpStatusCode.NotFound, attempt.StatusCode);
        Assert.Equal(0, await app.WithDbAsync(db => db.UserDevices.CountAsync()));
    }
}
