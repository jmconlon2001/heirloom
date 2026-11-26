using FamilyMemories.Tests.Infrastructure;
using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;

namespace FamilyMemories.E2E;

public sealed class LoginTests(PostgresFixture postgres, BrowsersFixture browsers) : IAsyncLifetime
{
    private readonly KestrelAppFactory app = new(postgres);

    public ValueTask InitializeAsync() => app.InitializeAsync();
    public ValueTask DisposeAsync() => app.DisposeAsync();

    private static Task AssertAccessibleAsync(IPage page, string because) => Accessibility.AssertAccessibleAsync(page, because);
    private static Task AssertNoHorizontalScrollAsync(IPage page) => Accessibility.AssertNoHorizontalScrollAsync(page);
    private static Task AssertTapTargetsAsync(IPage page) => Accessibility.AssertTapTargetsAsync(page);
    private static Task ScreenshotAsync(IPage page, string name) => Accessibility.ScreenshotAsync(page, name);

    private async Task SignInAsync(IPage page)
    {
        await page.GotoAsync("/");
        await page.GetByRole(AriaRole.Link, new() { Name = "Sign in" }).First.ClickAsync();
        await Expect(page).ToHaveURLAsync(new System.Text.RegularExpressions.Regex("/account/login"));
        await page.GetByLabel("Your email address").FillAsync(AppFactory.AdminEmail);
        await page.GetByRole(AriaRole.Button, new() { Name = "Email me a sign-in link" }).ClickAsync();
        await Expect(page.GetByRole(AriaRole.Heading, new() { Name = "Check your email" })).ToBeVisibleAsync();

        var link = CapturingEmailSender.ExtractMagicLink(app.Email.To(AppFactory.AdminEmail).Last());
        await page.GotoAsync(link.PathAndQuery);
        await page.GetByRole(AriaRole.Button, new() { Name = "Sign me in" }).ClickAsync();
        await Expect(page.GetByRole(AriaRole.Heading, new() { Name = "Hello, Jacob" })).ToBeVisibleAsync();
    }

    [Fact]
    public async Task IPad_magic_link_login_is_accessible_at_every_step()
    {
        await using var context = await browsers.NewIPadAsync(app.BaseUrl);
        var page = await context.NewPageAsync();

        await page.GotoAsync("/account/login");
        await ScreenshotAsync(page, "ipad-login");
        await AssertAccessibleAsync(page, "login page");
        await AssertTapTargetsAsync(page);

        await page.GetByLabel("Your email address").FillAsync(AppFactory.AdminEmail);
        await page.GetByRole(AriaRole.Button, new() { Name = "Email me a sign-in link" }).ClickAsync();
        await AssertAccessibleAsync(page, "check-email page");

        var link = CapturingEmailSender.ExtractMagicLink(app.Email.To(AppFactory.AdminEmail).Last());
        await page.GotoAsync(link.PathAndQuery);
        await AssertAccessibleAsync(page, "magic-link page");
        await page.GetByRole(AriaRole.Button, new() { Name = "Sign me in" }).ClickAsync();

        await Expect(page.GetByRole(AriaRole.Heading, new() { Name = "Hello, Jacob" })).ToBeVisibleAsync();
        await ScreenshotAsync(page, "ipad-home");
        await AssertAccessibleAsync(page, "home page");
        await AssertTapTargetsAsync(page);

        // Remembered device: a fresh tab in the same browser is still signed in.
        var again = await context.NewPageAsync();
        await again.GotoAsync("/");
        await Expect(again.GetByRole(AriaRole.Heading, new() { Name = "Hello, Jacob" })).ToBeVisibleAsync();
    }

    [Fact]
    public async Task IPad_at_A_plus_plus_with_high_contrast_stays_usable_and_remembers_the_choice()
    {
        await using var context = await browsers.NewIPadAsync(app.BaseUrl);
        var page = await context.NewPageAsync();
        await SignInAsync(page);

        await page.GetByRole(AriaRole.Button, new() { Name = "A++" }).ClickAsync();
        await page.GetByRole(AriaRole.Button, new() { Name = "High contrast" }).ClickAsync();
        await Expect(page.Locator("html")).ToHaveClassAsync("text-2 high-contrast");
        await Expect(page.GetByRole(AriaRole.Button, new() { Name = "A++" })).ToHaveAttributeAsync("aria-pressed", "true");

        var fontSize = await page.EvaluateAsync<string>("getComputedStyle(document.documentElement).fontSize");
        Assert.Equal("28px", fontSize);

        await page.ReloadAsync();
        await Expect(page.Locator("html")).ToHaveClassAsync("text-2 high-contrast");

        await ScreenshotAsync(page, "ipad-home-a++-high-contrast");
        await AssertAccessibleAsync(page, "home at A++ high contrast");
        await AssertNoHorizontalScrollAsync(page);
        await AssertTapTargetsAsync(page);

        await page.GotoAsync("/account/logout");
        await AssertAccessibleAsync(page, "sign-out page at A++");
        await AssertNoHorizontalScrollAsync(page);
    }

    [Fact]
    public async Task Desktop_at_200_percent_zoom_and_A_plus_plus_has_no_sideways_scrolling()
    {
        // 200% zoom on a 1280px-wide window leaves 640 CSS pixels.
        await using var context = await browsers.Chromium.NewContextAsync(new()
        {
            BaseURL = app.BaseUrl.ToString(),
            ViewportSize = new() { Width = 640, Height = 400 },
            DeviceScaleFactor = 2,
        });
        var page = await context.NewPageAsync();
        await page.GotoAsync("/account/login");
        await page.GetByRole(AriaRole.Button, new() { Name = "A++" }).ClickAsync();
        await Expect(page.Locator("html")).ToHaveClassAsync("text-2");

        await AssertNoHorizontalScrollAsync(page);
        await AssertAccessibleAsync(page, "login at 200% zoom, A++");
    }

    [Fact]
    public async Task Text_size_works_with_javascript_turned_off()
    {
        await using var context = await browsers.Chromium.NewContextAsync(new() { BaseURL = app.BaseUrl.ToString(), JavaScriptEnabled = false });
        var page = await context.NewPageAsync();
        await page.GotoAsync("/account/login");
        await page.GetByRole(AriaRole.Button, new() { Name = "A+" }).First.ClickAsync();
        await Expect(page.Locator("html")).ToHaveClassAsync("text-1");
        await Expect(page).ToHaveURLAsync(new System.Text.RegularExpressions.Regex("/account/login$"));
    }
}
