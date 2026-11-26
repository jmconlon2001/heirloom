using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using FamilyMemories.Data;
using FamilyMemories.Tests.Infrastructure;
using Microsoft.AspNetCore.Mvc.Testing;

namespace FamilyMemories.Tests;

public sealed class StatusTests(PostgresFixture postgres) : IAsyncLifetime
{
    private sealed class StatusFactory(PostgresFixture postgres, Dictionary<string, string?> extra) : AppFactory(postgres)
    {
        protected override IDictionary<string, string?> Settings => extra;
    }

    private static Dictionary<string, string?> Base() => new()
    {
        ["SMTP_HOST"] = "smtp.example.test",
        ["STATUS_TOKEN"] = "monitor-secret-123",
    };

    private readonly StatusFactory app = new(postgres, Base());
    public ValueTask InitializeAsync() => app.InitializeAsync();
    public ValueTask DisposeAsync() => app.DisposeAsync();

    private HttpClient Anonymous() => app.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

    private static async Task<JsonElement> Json(HttpResponseMessage r) => JsonDocument.Parse(await r.Content.ReadAsStringAsync()).RootElement;

    [Fact]
    public async Task Anyone_gets_only_the_overall_state_from_the_status_api()
    {
        var response = await Anonymous().GetAsync("/api/status");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);                   // warnings (no backups, no PDF maker) are still 200
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
        var json = await Json(response);
        Assert.Equal("warning", json.GetProperty("status").GetString());
        Assert.True(json.TryGetProperty("checkedAt", out _));
        Assert.False(json.TryGetProperty("version", out _));
        Assert.False(json.TryGetProperty("components", out _));
    }

    [Fact]
    public async Task Admins_and_monitors_with_the_token_get_every_component()
    {
        var admin = await Browser.SignedInAsync(app, AppFactory.AdminEmail);
        var asAdmin = await Json(await admin.Http.GetAsync("/api/status"));
        Assert.Equal("0.0.0-dev", asAdmin.GetProperty("version").GetString());
        Assert.True(asAdmin.GetProperty("uptimeSeconds").GetInt64() >= 0);
        var parts = asAdmin.GetProperty("components").EnumerateArray().ToDictionary(c => c.GetProperty("key").GetString()!, c => c);
        Assert.Equal(["website", "database", "pdf", "backups", "email", "jobs", "storage"], parts.Keys.ToArray());
        Assert.Equal("ok", parts["database"].GetProperty("status").GetString());
        Assert.Equal("warning", parts["backups"].GetProperty("status").GetString());          // RESTIC_PASSWORD not set
        Assert.Equal("warning", parts["pdf"].GetProperty("status").GetString());              // GOTENBERG_URL not set
        Assert.Equal("ok", parts["email"].GetProperty("status").GetString());
        Assert.True(parts["database"].GetProperty("details").GetProperty("sizeBytes").GetInt64() > 0);

        var monitor = Anonymous();
        monitor.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "monitor-secret-123");
        Assert.True((await Json(await monitor.GetAsync("/api/status"))).TryGetProperty("components", out _));

        var wrong = Anonymous();
        wrong.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "monitor-secret-124");
        Assert.False((await Json(await wrong.GetAsync("/api/status"))).TryGetProperty("components", out _));

        // A family member isn't an admin: overall state only.
        await app.CreateUserAsync("Lily", "lily-status@example.test", [Roles.Family]);
        var lily = await Browser.SignedInAsync(app, "lily-status@example.test");
        Assert.False((await Json(await lily.Http.GetAsync("/api/status"))).TryGetProperty("components", out _));
    }

    [Fact]
    public async Task The_status_page_shows_each_part_to_anyone_and_the_details_only_to_admins()
    {
        var page = WebUtility.HtmlDecode(await Anonymous().GetStringAsync("/status"));
        Assert.Contains("Is everything working?", page);
        Assert.Contains("Everything is working, but something needs the family admin's attention.", page);
        Assert.Contains("Database", page);
        Assert.Contains("Backups", page);
        Assert.DoesNotContain("RESTIC_PASSWORD", page);                      // no details for the public
        Assert.DoesNotContain("0.0.0-dev", page);

        var admin = await Browser.SignedInAsync(app, AppFactory.AdminEmail);
        var adminPage = WebUtility.HtmlDecode(await admin.Http.GetStringAsync("/status"));
        Assert.Contains("RESTIC_PASSWORD is empty", adminPage);
        Assert.Contains("0.0.0-dev", adminPage);
        Assert.Contains("/api/status", adminPage);
        Assert.Contains("System status", WebUtility.HtmlDecode(await admin.Http.GetStringAsync("/admin")));
    }

    [Fact]
    public async Task The_landing_page_links_to_the_project_on_GitHub_and_to_the_status_page()
    {
        var landing = await Anonymous().GetStringAsync("/");
        Assert.Contains("href=\"https://github.com/jmconlon2001/heirloom\"", landing);
        Assert.Contains("rel=\"noopener\"", landing);
        Assert.Contains("href=\"/status\"", landing);
    }

    [Fact]
    public async Task Anyone_signed_in_can_switch_the_whole_site_between_simple_and_detailed_and_it_is_remembered_per_person()
    {
        var admin = await Browser.SignedInAsync(app, AppFactory.AdminEmail);
        var simple = await admin.Http.GetStringAsync("/library");
        Assert.Contains("aria-labelledby=\"view-label\"", simple);                   // the switch is in the header on every page
        Assert.DoesNotContain("view-detailed", simple);
        Assert.Contains("class=\"wrap main-nav\"", simple);

        var switched = await admin.Http.PostFormAsync("/view", ViewForm(simple), ("view", "detailed"), ("returnUrl", "/admin/backups"));
        Assert.Equal(HttpStatusCode.Redirect, switched.StatusCode);
        Assert.Equal("/admin/backups", switched.Headers.Location?.OriginalString);

        // Detailed on family pages too: a sidebar instead of the top menu.
        var library = WebUtility.HtmlDecode(await admin.Http.GetStringAsync("/library"));
        Assert.Contains("view-detailed", library);
        Assert.Contains("class=\"side-nav\"", library);
        Assert.DoesNotContain("class=\"wrap main-nav\"", library);
        Assert.Contains("Timeline", library);
        Assert.Contains("People and invitations", library);                          // admins also get the admin sections
        var dashboard = WebUtility.HtmlDecode(await admin.Http.GetStringAsync("/admin"));
        Assert.Contains("finished stories", dashboard);
        Assert.Contains("app-main-wide", dashboard);
        Assert.DoesNotContain("app-main-wide", library);                             // reading pages keep a readable width
        Assert.Contains("view-detailed", await admin.Http.GetStringAsync("/status"));

        // Remembered on another device (a new sign-in), but only for that person.
        var sameAdminElsewhere = await Browser.SignedInAsync(app, AppFactory.AdminEmail);
        Assert.Contains("view-detailed", await sameAdminElsewhere.Http.GetStringAsync("/"));
        await app.CreateUserAsync("Lily", "lily-view@example.test", [Roles.Family]);
        var lily = await Browser.SignedInAsync(app, "lily-view@example.test");
        var lilyHome = await lily.Http.GetStringAsync("/");
        Assert.DoesNotContain("view-detailed", lilyHome);                            // Simple is the default

        // A family member can choose Detailed too, and sees no admin sections.
        await lily.Http.PostFormAsync("/view", ViewForm(lilyHome), ("view", "detailed"), ("returnUrl", "/library"));
        var lilyLibrary = WebUtility.HtmlDecode(await lily.Http.GetStringAsync("/library"));
        Assert.Contains("view-detailed", lilyLibrary);
        Assert.DoesNotContain("People and invitations", lilyLibrary);

        // And back again, never redirecting off-site.
        var back = await admin.Http.PostFormAsync("/view", ViewForm(library), ("view", "simple"), ("returnUrl", "//evil.example"));
        Assert.Equal("/", back.Headers.Location?.OriginalString);
        Assert.DoesNotContain("view-detailed", await admin.Http.GetStringAsync("/admin"));
    }

    [Fact]
    public async Task Signed_out_visitors_get_no_view_switch_and_cannot_post_one()
    {
        var client = Anonymous();
        Assert.DoesNotContain("view-label", await client.GetStringAsync("/"));
        var login = await client.GetStringAsync("/account/login");
        var response = await client.PostFormAsync("/view", login, ("view", "detailed"));
        Assert.NotEqual("/", response.Headers.Location?.OriginalString);
        Assert.False(await app.WithDbAsync(db => Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.AnyAsync(db.Settings, s => s.Key.StartsWith("view-mode:"))));
    }

    private static string ViewForm(string html) =>
        System.Text.RegularExpressions.Regex.Match(html, "<form class=\"display-controls view-controls\".*?</form>", System.Text.RegularExpressions.RegexOptions.Singleline).Value;
}

public sealed class StatusProblemTests(PostgresFixture postgres) : IAsyncLifetime
{
    private sealed class BrokenFactory(PostgresFixture postgres) : AppFactory(postgres)
    {
        protected override IDictionary<string, string?> Settings => new Dictionary<string, string?>
        {
            ["SMTP_HOST"] = "smtp.example.test",
            ["GOTENBERG_URL"] = "http://127.0.0.1:9",       // nothing listens here
            ["PROJECT_URL"] = "",
        };
    }

    private readonly BrokenFactory app = new(postgres);
    public ValueTask InitializeAsync() => app.InitializeAsync();
    public ValueTask DisposeAsync() => app.DisposeAsync();

    [Fact]
    public async Task A_broken_part_makes_the_status_api_answer_503_and_the_github_link_can_be_hidden()
    {
        var client = app.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var response = await client.GetAsync("/api/status");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal("problem", JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.GetProperty("status").GetString());

        var page = await client.GetAsync("/status");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, page.StatusCode);
        Assert.Contains("Something isn't working right now.", WebUtility.HtmlDecode(await page.Content.ReadAsStringAsync()));

        Assert.DoesNotContain("github.com", await client.GetStringAsync("/"));
    }
}

public sealed class StatusSpeechToTextTests(PostgresFixture postgres) : IAsyncLifetime
{
    private sealed class NoWhisperFactory(PostgresFixture postgres) : AppFactory(postgres)
    {
        protected override IDictionary<string, string?> Settings => new Dictionary<string, string?>
        {
            ["SMTP_HOST"] = "smtp.example.test",
            ["WHISPER_URL"] = "http://127.0.0.1:9",       // speech-to-text on (the default) but nothing listening
            ["STATUS_TOKEN"] = "monitor-secret-123",
        };
    }

    private readonly NoWhisperFactory app = new(postgres);
    public ValueTask InitializeAsync() => app.InitializeAsync();
    public ValueTask DisposeAsync() => app.DisposeAsync();

    [Fact]
    public async Task Speech_to_text_being_down_is_a_warning_not_an_outage()
    {
        var client = app.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "monitor-secret-123");
        var response = await client.GetAsync("/api/status");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);                    // monitors don't page anyone
        var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        var speech = json.GetProperty("components").EnumerateArray().Single(c => c.GetProperty("key").GetString() == "transcription");
        Assert.Equal("warning", speech.GetProperty("status").GetString());
        Assert.Contains("FEATURE_TRANSCRIPTION=false", speech.GetProperty("summary").GetString());
    }
}
