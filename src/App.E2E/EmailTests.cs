using System.Text.RegularExpressions;
using FamilyMemories.Data;
using FamilyMemories.Email;
using FamilyMemories.Tests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;

namespace FamilyMemories.E2E;

/// <summary>Phase 4 pages: the email settings page (from a link, no sign-in) and the admin wording editor.</summary>
public sealed class EmailTests(PostgresFixture postgres, BrowsersFixture browsers) : IAsyncLifetime
{
    private readonly KestrelAppFactory app = new(postgres);
    public ValueTask InitializeAsync() => app.InitializeAsync();
    public ValueTask DisposeAsync() => app.DisposeAsync();

    [Fact]
    public async Task IPad_email_settings_page_snoozes_with_one_big_button()
    {
        var rose = await app.CreateUserAsync("Rose Smith", "email-e2e@example.test", [Roles.Storyteller], PromptSetKind.Core);
        var url = app.Services.GetRequiredService<EmailLinks>().PreferencesUrl(rose.Id);
        await using var context = await browsers.NewIPadAsync(app.BaseUrl);
        var page = await context.NewPageAsync();
        await page.GotoAsync(new Uri(url).PathAndQuery);
        await Expect(page.GetByRole(AriaRole.Heading, new() { Name = "Email settings for Rose Smith" })).ToBeVisibleAsync();
        await Accessibility.AssertAccessibleAsync(page, "email settings page");
        await Accessibility.AssertTapTargetsAsync(page);
        await page.GetByRole(AriaRole.Button, new() { Name = "Remind me in 2 weeks" }).ClickAsync();
        await Expect(page.GetByText("We'll send the next question in two weeks.")).ToBeVisibleAsync();
        await Expect(page.GetByText(new Regex("Taking a break until"))).ToBeVisibleAsync();
    }

    [Fact]
    public async Task Admin_edits_email_wording_with_a_live_preview()
    {
        await using var context = await browsers.Chromium.NewContextAsync(new() { BaseURL = app.BaseUrl.ToString() });
        var page = await context.NewPageAsync();
        await page.GotoAsync("/account/login");
        await page.GetByLabel("Your email address").FillAsync(AppFactory.AdminEmail);
        await page.GetByRole(AriaRole.Button, new() { Name = "Email me a sign-in link" }).ClickAsync();
        await Expect(page.GetByRole(AriaRole.Heading, new() { Name = "Check your email" })).ToBeVisibleAsync();
        await page.GotoAsync(CapturingEmailSender.ExtractMagicLink(app.Email.To(AppFactory.AdminEmail).Last()).PathAndQuery);
        await page.GetByRole(AriaRole.Button, new() { Name = "Sign me in" }).ClickAsync();

        await page.GotoAsync("/admin/email/templates/Reminder");
        var preview = page.FrameLocator("iframe[name=email-preview]");
        await Expect(preview.GetByText("Where and when were you born?")).ToBeVisibleAsync();
        await page.GetByLabel("Subject").FillAsync("Hello {{ name }}, a question from your family");
        await page.GetByRole(AriaRole.Button, new() { Name = "Preview" }).ClickAsync();
        await Expect(preview.GetByText("Hello Rose, a question from your family")).ToBeVisibleAsync();
        await Accessibility.AssertAccessibleAsync(page, "email wording editor");
    }
}
