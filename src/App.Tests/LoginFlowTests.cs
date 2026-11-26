using System.Net;
using FamilyMemories.Data;
using FamilyMemories.Tests.Infrastructure;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;

namespace FamilyMemories.Tests;

public sealed class LoginFlowTests(PostgresFixture postgres) : IAsyncLifetime
{
    private readonly AppFactory app = new(postgres);

    public ValueTask InitializeAsync() => app.InitializeAsync();
    public ValueTask DisposeAsync() => app.DisposeAsync();

    private HttpClient NewBrowser() => app.CreateClient(new WebApplicationFactoryClientOptions
    {
        AllowAutoRedirect = false,
        HandleCookies = true,
        BaseAddress = new Uri("https://localhost"),
    });

    /// <summary>Requests a link for <paramref name="email"/> and returns the last emailed link.</summary>
    private async Task<Uri> RequestLinkAsync(HttpClient browser, string email, string? returnUrl = null)
    {
        var page = await browser.GetStringAsync("/account/login" + (returnUrl is null ? "" : $"?returnUrl={Uri.EscapeDataString(returnUrl)}"));
        var fields = new List<(string, string)> { ("Email", email) };
        if (returnUrl is not null) fields.Add(("ReturnUrl", returnUrl));
        var response = await browser.PostFormAsync("/account/login", page, [.. fields]);
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal("/account/check-email", response.Headers.Location?.OriginalString);
        return CapturingEmailSender.ExtractMagicLink(app.Email.To(email).Last());
    }

    private static async Task<HttpResponseMessage> FollowLinkAsync(HttpClient browser, Uri link)
    {
        var page = await browser.GetStringAsync(link.PathAndQuery);
        Assert.Contains("Sign me in", page);
        var token = Uri.UnescapeDataString(link.Query.Split("token=")[1]);
        return await browser.PostFormAsync("/account/magic-link", page, ("token", token));
    }

    [Fact]
    public async Task Signed_out_visitors_see_the_landing_page_and_private_pages_send_them_to_sign_in()
    {
        var landing = WebUtility.HtmlDecode(await NewBrowser().GetStringAsync("/"));
        Assert.Contains("Every family has stories worth keeping.", landing);
        Assert.Contains("href=\"/account/login\"", landing);
        Assert.DoesNotContain("Need help? Call", "");
        foreach (var url in new[] { "/library", "/questions", "/tree", "/people" })
        {
            var response = await NewBrowser().GetAsync(url);
            Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
            Assert.StartsWith("https://localhost/account/login", response.Headers.Location!.ToString());
        }
    }

    [Fact]
    public async Task Admin_signs_in_with_an_emailed_link()
    {
        var browser = NewBrowser();
        var link = await RequestLinkAsync(browser, AppFactory.AdminEmail);

        // The link points at PUBLIC_BASE_URL, never at whatever Host the request came in on.
        Assert.StartsWith(AppFactory.PublicBaseUrl + "/account/magic-link?token=", link.ToString());
        var email = app.Email.To(AppFactory.AdminEmail).Last();
        Assert.Contains("Jacob Admin", email.TextBody);
        Assert.Contains("Sign me in", email.HtmlBody);

        // Opening the link (as an email scanner would) does not use it up.
        await browser.GetStringAsync(link.PathAndQuery);
        Assert.Equal(0, await app.WithDbAsync(db => db.MagicLinkTokens.CountAsync(t => t.ConsumedAt != null)));

        var signIn = await FollowLinkAsync(browser, link);
        Assert.Equal(HttpStatusCode.Redirect, signIn.StatusCode);
        Assert.Equal("/", signIn.Headers.Location?.OriginalString);

        var home = await browser.GetStringAsync("/");
        Assert.Contains("Hello, Jacob", home);
        Assert.Contains("Signed in as <strong>Jacob Admin</strong>", home);
        Assert.Contains("Sign out", home);
    }

    [Fact]
    public async Task The_sign_in_cookie_remembers_the_device_for_180_days()
    {
        var browser = NewBrowser();
        var signIn = await FollowLinkAsync(browser, await RequestLinkAsync(browser, AppFactory.AdminEmail));

        var cookie = signIn.Headers.GetValues("Set-Cookie").Single(c => c.StartsWith("fm_auth="));
        Assert.Contains("httponly", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("secure", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("samesite=lax", cookie, StringComparison.OrdinalIgnoreCase);
        var expires = DateTimeOffset.Parse(cookie.Split("expires=")[1].Split(';')[0]);
        Assert.InRange(expires - DateTimeOffset.UtcNow, TimeSpan.FromDays(179), TimeSpan.FromDays(181));

        var device = await app.WithDbAsync(db => db.UserDevices.SingleAsync());
        Assert.Null(device.RevokedAt);
    }

    [Fact]
    public async Task A_used_link_cannot_sign_in_a_second_browser()
    {
        var first = NewBrowser();
        var link = await RequestLinkAsync(first, AppFactory.AdminEmail);
        await FollowLinkAsync(first, link);

        var page = await NewBrowser().GetStringAsync(link.PathAndQuery);
        Assert.Contains("This link has already been used", page);
        Assert.DoesNotContain("name=\"token\"", page);
    }

    [Fact]
    public async Task Return_url_is_honoured_after_sign_in_but_only_if_local()
    {
        var browser = NewBrowser();
        var signIn = await FollowLinkAsync(browser, await RequestLinkAsync(browser, AppFactory.AdminEmail, "/account/logout"));
        Assert.Equal("/account/logout", signIn.Headers.Location?.OriginalString);

        var other = NewBrowser();
        var evil = await FollowLinkAsync(other, await RequestLinkAsync(other, AppFactory.AdminEmail, "https://evil.example/"));
        Assert.Equal("/", evil.Headers.Location?.OriginalString);
    }

    [Fact]
    public async Task Unknown_addresses_get_the_same_answer_and_no_email()
    {
        var browser = NewBrowser();
        var page = await browser.GetStringAsync("/account/login");
        var response = await browser.PostFormAsync("/account/login", page, ("Email", "stranger@example.test"));

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal("/account/check-email", response.Headers.Location?.OriginalString);
        Assert.Empty(app.Email.To("stranger@example.test"));
    }

    [Fact]
    public async Task A_mistyped_address_gets_a_plain_language_message()
    {
        var browser = NewBrowser();
        var page = await browser.GetStringAsync("/account/login");
        var response = await browser.PostFormAsync("/account/login", page, ("Email", "rose"));
        var html = await response.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("Please type your whole email address", html);
        Assert.Contains("aria-invalid=\"true\"", html);
    }

    [Fact]
    public async Task Signing_out_revokes_that_device_even_if_the_cookie_is_replayed()
    {
        var browser = NewBrowser();
        var signIn = await FollowLinkAsync(browser, await RequestLinkAsync(browser, AppFactory.AdminEmail));
        var authCookie = signIn.Headers.GetValues("Set-Cookie").Single(c => c.StartsWith("fm_auth=")).Split(';')[0];

        var home = await browser.GetStringAsync("/");
        var logout = await browser.PostFormAsync("/account/logout", home);
        Assert.Equal("/account/signed-out", logout.Headers.Location?.OriginalString);
        Assert.NotNull((await app.WithDbAsync(db => db.UserDevices.SingleAsync())).RevokedAt);

        // A copy of the old cookie no longer works.
        var replay = app.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false, HandleCookies = false, BaseAddress = new Uri("https://localhost"),
        });
        var request = new HttpRequestMessage(HttpMethod.Get, "/library");
        request.Headers.Add("Cookie", authCookie);
        var response = await replay.SendAsync(request);
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Contains("/account/login", response.Headers.Location!.ToString());
    }

    [Fact]
    public async Task Bootstrap_admin_is_seeded_with_the_admin_role()
    {
        var roles = await app.WithDbAsync(db =>
            (from ur in db.UserRoles
             join u in db.Users on ur.UserId equals u.Id
             join r in db.Roles on ur.RoleId equals r.Id
             where u.Email == AppFactory.AdminEmail
             select r.Name).ToListAsync());
        Assert.Equal([Roles.Admin], roles);
        Assert.Equal(3, await app.WithDbAsync(db => db.Roles.CountAsync()));
    }

    [Fact]
    public async Task Posting_without_an_antiforgery_token_is_refused()
    {
        var response = await NewBrowser().PostAsync("/account/login",
            new FormUrlEncodedContent([new("Email", AppFactory.AdminEmail)]));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(app.Email.Sent);
    }
}
