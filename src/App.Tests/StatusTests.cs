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
    public async Task Admins_can_switch_between_basic_and_complex_views_and_it_is_remembered_per_admin()
    {
        var admin = await Browser.SignedInAsync(app, AppFactory.AdminEmail);
        var basic = await admin.Http.GetStringAsync("/admin");
        Assert.Contains("Admin view", basic);
        Assert.DoesNotContain("admin-complex", basic);
        Assert.DoesNotContain("admin-nav", basic);

        var switched = await admin.Http.PostFormAsync("/admin/view", ToggleForm(basic), ("mode", "complex"), ("returnUrl", "/admin/backups"));
        Assert.Equal(HttpStatusCode.Redirect, switched.StatusCode);
        Assert.Equal("/admin/backups", switched.Headers.Location?.OriginalString);

        var complex = WebUtility.HtmlDecode(await admin.Http.GetStringAsync("/admin"));
        Assert.Contains("admin-complex", complex);
        Assert.Contains("class=\"admin-nav\"", complex);
        Assert.Contains("finished stories", complex);                        // the dashboard
        Assert.Contains("Recent activity", complex);
        Assert.Contains("admin-complex", await admin.Http.GetStringAsync("/admin/users"));
        Assert.Contains("admin-complex", await admin.Http.GetStringAsync("/status"));
        // The family pages keep the family look.
        Assert.DoesNotContain("admin-complex", await admin.Http.GetStringAsync("/library"));

        // Remembered on another device (a new sign-in), but not for a different admin.
        var sameAdminElsewhere = await Browser.SignedInAsync(app, AppFactory.AdminEmail);
        Assert.Contains("admin-complex", await sameAdminElsewhere.Http.GetStringAsync("/admin"));
        await app.CreateUserAsync("Second Admin", "admin2@example.test", [Roles.Admin]);
        var other = await Browser.SignedInAsync(app, "admin2@example.test");
        Assert.DoesNotContain("admin-complex", await other.Http.GetStringAsync("/admin"));

        // And back again.
        var back = await admin.Http.PostFormAsync("/admin/view", ToggleForm(complex), ("mode", "basic"), ("returnUrl", "//evil.example"));
        Assert.Equal("/admin", back.Headers.Location?.OriginalString);                 // never an off-site redirect
        Assert.DoesNotContain("admin-complex", await admin.Http.GetStringAsync("/admin"));
    }

    [Fact]
    public async Task Only_admins_can_change_the_admin_view()
    {
        await app.CreateUserAsync("Lily", "lily-view@example.test", [Roles.Family]);
        var lily = await Browser.SignedInAsync(app, "lily-view@example.test");
        var home = await lily.Http.GetStringAsync("/");
        Assert.DoesNotContain("Admin view", home);
        var response = await lily.Http.PostFormAsync("/admin/view", home, ("mode", "complex"));
        Assert.NotEqual("/admin", response.Headers.Location?.OriginalString);          // turned away, not switched
        Assert.False(await app.WithDbAsync(db => Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.AnyAsync(db.Settings, s => s.Key.StartsWith("admin-view:"))));
    }

    private static string ToggleForm(string html) =>
        System.Text.RegularExpressions.Regex.Match(html, "<form class=\"admin-view-toggle\".*?</form>", System.Text.RegularExpressions.RegexOptions.Singleline).Value;
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
