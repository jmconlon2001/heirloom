using System.Net;
using FamilyMemories.Tests.Infrastructure;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;

namespace FamilyMemories.Tests;

public sealed class PlatformTests(PostgresFixture postgres) : IAsyncLifetime
{
    private readonly ProxyAppFactory app = new(postgres);

    public ValueTask InitializeAsync() => app.InitializeAsync();
    public ValueTask DisposeAsync() => app.DisposeAsync();

    private sealed class ProxyAppFactory(PostgresFixture postgres) : AppFactory(postgres)
    {
        protected override IDictionary<string, string?> Settings => new Dictionary<string, string?>
        {
            ["TRUSTED_PROXIES"] = "172.20.0.0/16",
        };
    }

    private HttpClient Browser() => app.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

    [Fact]
    public async Task Liveness_is_always_ok()
    {
        var response = await Browser().GetAsync("/healthz");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Readiness_checks_the_database_and_reports_missing_gotenberg_as_degraded()
    {
        var response = await Browser().GetAsync("/readyz");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("Degraded", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Every_page_sends_strict_security_headers()
    {
        var response = await Browser().GetAsync("/account/login");
        var csp = response.Headers.GetValues("Content-Security-Policy").Single();
        Assert.Contains("script-src 'self'", csp);
        Assert.DoesNotContain("unsafe-inline", csp);
        Assert.Contains("frame-ancestors 'none'", csp);
        Assert.Equal("nosniff", response.Headers.GetValues("X-Content-Type-Options").Single());
        Assert.Equal("DENY", response.Headers.GetValues("X-Frame-Options").Single());
    }

    [Fact]
    public async Task Pages_have_no_inline_scripts_or_styles()
    {
        var html = await Browser().GetStringAsync("/account/login");
        Assert.DoesNotMatch("<script(?![^>]*\\bsrc=)", html);
        Assert.DoesNotContain("style=\"", html);
    }

    [Fact]
    public async Task Every_page_shows_need_help_with_the_admin_phone()
    {
        var html = await Browser().GetStringAsync("/account/login");
        Assert.Contains("Need help?", html);
        Assert.Contains("href=\"tel:5550100199\"", html);
        Assert.Contains("(555) 010-0199", html);
    }

    [Fact]
    public async Task Text_size_and_contrast_work_without_javascript()
    {
        var browser = app.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = true });
        var page = await browser.GetStringAsync("/account/login");
        Assert.Contains("class=\"text-0\"", page);

        var response = await browser.PostFormAsync("/display", page, ("size", "2"), ("returnUrl", "/account/login"));
        var html = await response.Content.ReadAsStringAsync();
        Assert.Contains("class=\"text-2\"", html);

        response = await browser.PostFormAsync("/display", html, ("contrast", "on"), ("returnUrl", "/account/login"));
        html = await response.Content.ReadAsStringAsync();
        Assert.Contains("class=\"text-2 high-contrast\"", html);
        Assert.Contains("aria-pressed=\"true\"", html);
    }

    [Fact]
    public async Task Unknown_pages_get_a_friendly_message_without_codes()
    {
        var response = await Browser().GetAsync("/account/no-such-page");
        var html = WebUtility.HtmlDecode(await response.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Contains("We couldn't find that page", html);
        Assert.DoesNotContain("404", html);
    }

    [Fact]
    public async Task Forwarded_proto_is_honoured_from_a_trusted_proxy()
    {
        var context = await app.Server.SendAsync(c =>
        {
            c.Request.Method = "GET";
            c.Request.Path = "/account/login";
            c.Request.Scheme = "http";
            c.Connection.RemoteIpAddress = IPAddress.Parse("172.20.0.5");
            c.Request.Headers["X-Forwarded-Proto"] = "https";
            c.Request.Headers["X-Forwarded-For"] = "203.0.113.9";
        });
        var antiforgeryCookie = context.Response.Headers.SetCookie.ToString();
        Assert.Contains("fm_af", antiforgeryCookie);
        Assert.Contains("secure", antiforgeryCookie, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Forwarded_headers_from_an_untrusted_address_are_ignored()
    {
        var context = await app.Server.SendAsync(c =>
        {
            c.Request.Method = "GET";
            c.Request.Path = "/account/login";
            c.Request.Scheme = "http";
            c.Connection.RemoteIpAddress = IPAddress.Parse("198.51.100.7");
            c.Request.Headers["X-Forwarded-Proto"] = "https";
        });
        Assert.DoesNotContain("secure", context.Response.Headers.SetCookie.ToString(), StringComparison.OrdinalIgnoreCase);
    }
}

public sealed class RateLimitTests(PostgresFixture postgres) : IAsyncLifetime
{
    private readonly StrictFactory app = new(postgres);

    public ValueTask InitializeAsync() => app.InitializeAsync();
    public ValueTask DisposeAsync() => app.DisposeAsync();

    private sealed class StrictFactory(PostgresFixture postgres) : AppFactory(postgres)
    {
        protected override IDictionary<string, string?> Settings => new Dictionary<string, string?> { ["AUTH_RATE_LIMIT"] = "3" };
    }

    [Fact]
    public async Task Sign_in_posts_are_rate_limited_with_a_friendly_page()
    {
        var browser = app.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var page = await browser.GetStringAsync("/account/login");
        for (var i = 0; i < 3; i++)
            Assert.Equal(HttpStatusCode.Redirect, (await browser.PostFormAsync("/account/login", page, ("Email", $"x{i}@example.test"))).StatusCode);

        var limited = await browser.PostFormAsync("/account/login", page, ("Email", "x@example.test"));
        Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode);
        Assert.Contains("Please wait 10 minutes", await limited.Content.ReadAsStringAsync());

        // Reading pages is never limited.
        Assert.Equal(HttpStatusCode.OK, (await browser.GetAsync("/account/login")).StatusCode);
    }
}
