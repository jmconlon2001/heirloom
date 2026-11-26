using System.Net;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc.Testing;

namespace FamilyMemories.Tests.Infrastructure;

/// <summary>An HTTP "browser" with cookies, for driving pages like a person would.</summary>
public sealed partial class Browser(AppFactory app)
{
    public HttpClient Http { get; } = app.CreateClient(new WebApplicationFactoryClientOptions
    {
        AllowAutoRedirect = false,
        HandleCookies = true,
        BaseAddress = new Uri("https://localhost"),
    });

    public static async Task<Browser> SignedInAsync(AppFactory app, string email)
    {
        var browser = new Browser(app);
        await browser.SignInAsync(email);
        return browser;
    }

    public async Task SignInAsync(string email)
    {
        var page = await Http.GetStringAsync("/account/login");
        var post = await Http.PostFormAsync("/account/login", page, ("Email", email));
        if (post.StatusCode != HttpStatusCode.Redirect) throw new InvalidOperationException("Login post failed");
        // With Hangfire on, the email is sent by a background job a moment later.
        for (var i = 0; i < 100 && app.Email.To(email).Count == 0; i++) await Task.Delay(100);
        var link = CapturingEmailSender.ExtractMagicLink(app.Email.To(email).Last());
        await RedeemAsync(link);
    }

    public async Task<HttpResponseMessage> RedeemAsync(Uri link)
    {
        var confirm = await Http.GetStringAsync(link.PathAndQuery);
        var token = Uri.UnescapeDataString(link.Query.Split("token=")[1]);
        var response = await Http.PostFormAsync("/account/magic-link", confirm, ("token", token));
        if (response.StatusCode != HttpStatusCode.Redirect) throw new InvalidOperationException("Magic link failed");
        return response;
    }

    /// <summary>Follows a redirect chain manually and returns the final page HTML and path.</summary>
    public async Task<(string Path, string Html, HttpStatusCode Status)> GetAsync(string url)
    {
        var response = await Http.GetAsync(url);
        for (var i = 0; i < 5 && response.StatusCode is HttpStatusCode.Redirect or HttpStatusCode.Found; i++)
        {
            url = response.Headers.Location!.IsAbsoluteUri ? response.Headers.Location.PathAndQuery : response.Headers.Location.OriginalString;
            response = await Http.GetAsync(url);
        }
        return (url, await response.Content.ReadAsStringAsync(), response.StatusCode);
    }

    /// <summary>The antiforgery token rendered into the writing page for scripts.</summary>
    public static string CsrfFromPage(string html) => WebUtility.HtmlDecode(CsrfRegex().Match(html).Groups[1].Value);

    [GeneratedRegex("data-csrf=\"([^\"]+)\"")]
    private static partial Regex CsrfRegex();
}
