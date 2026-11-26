using System.Text.RegularExpressions;
using FamilyMemories.Tests.Infrastructure;
using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;

namespace FamilyMemories.E2E;

/// <summary>The status page and the Simple / Detailed views, on an iPad and on a phone.</summary>
public sealed class ViewModeTests(PostgresFixture postgres, BrowsersFixture browsers) : IAsyncLifetime
{
    private readonly KestrelAppFactory app = new(postgres);
    public ValueTask InitializeAsync() => app.InitializeAsync();
    public ValueTask DisposeAsync() => app.DisposeAsync();

    [Fact]
    public async Task Status_page_and_both_views_are_accessible_and_the_choice_sticks()
    {
        await using var context = await browsers.NewIPadAsync(app.BaseUrl);
        var page = await context.NewPageAsync();

        await page.GotoAsync("/status");
        await Expect(page.GetByRole(AriaRole.Heading, new() { Name = "Is everything working?" })).ToBeVisibleAsync();
        await Accessibility.AssertAccessibleAsync(page, "public status page");

        await page.GotoAsync("/account/login");
        await page.GetByLabel("Your email address").FillAsync(AppFactory.AdminEmail);
        await page.GetByRole(AriaRole.Button, new() { Name = "Email me a sign-in link" }).ClickAsync();
        await Expect(page.GetByRole(AriaRole.Heading, new() { Name = "Check your email" })).ToBeVisibleAsync();   // the email has been sent
        await page.GotoAsync(CapturingEmailSender.ExtractMagicLink(app.Email.To(AppFactory.AdminEmail).Last()).PathAndQuery);
        await page.GetByRole(AriaRole.Button, new() { Name = "Sign me in" }).ClickAsync();
        await Expect(page.GetByRole(AriaRole.Heading, new() { NameRegex = new Regex("^Hello") })).ToBeVisibleAsync();

        await page.GotoAsync("/admin");
        await Expect(page.GetByRole(AriaRole.Button, new() { Name = "Simple" })).ToHaveAttributeAsync("aria-pressed", "true");
        await Accessibility.AssertAccessibleAsync(page, "admin home, simple view");
        await Accessibility.AssertTapTargetsAsync(page);                       // the new switch keeps 48px targets

        await page.GetByRole(AriaRole.Button, new() { Name = "Detailed" }).ClickAsync();
        var sections = page.GetByRole(AriaRole.Navigation, new() { Name = "Sections" });
        await Expect(sections).ToBeVisibleAsync();
        await Expect(page.GetByText("finished stories")).ToBeVisibleAsync();
        await Accessibility.AssertAccessibleAsync(page, "admin home, detailed view");
        await Accessibility.AssertNoHorizontalScrollAsync(page);

        await sections.GetByRole(AriaRole.Link, new() { Name = "People and invitations" }).ClickAsync();
        await Expect(page.GetByRole(AriaRole.Heading, new() { Name = "People" })).ToBeVisibleAsync();
        await Accessibility.AssertAccessibleAsync(page, "people, detailed view");

        await sections.GetByRole(AriaRole.Link, new() { Name = "Family Library" }).ClickAsync();
        await Expect(page.GetByRole(AriaRole.Heading, new() { Name = "Family Library", Exact = true })).ToBeVisibleAsync();
        await Accessibility.AssertAccessibleAsync(page, "family library, detailed view");
        await Accessibility.AssertNoHorizontalScrollAsync(page);

        await page.GotoAsync("/status");
        await Expect(page.GetByText("For the technically minded")).ToBeVisibleAsync();
        await Accessibility.AssertAccessibleAsync(page, "status page, detailed view");

        // Remembered after a reload; back to Simple with one tap.
        await page.GotoAsync("/");
        await Expect(page.GetByRole(AriaRole.Button, new() { Name = "Detailed" })).ToHaveAttributeAsync("aria-pressed", "true");
        await page.GetByRole(AriaRole.Button, new() { Name = "Simple" }).ClickAsync();
        await Expect(page.GetByRole(AriaRole.Navigation, new() { Name = "Sections" })).ToHaveCountAsync(0);
        await Expect(page.GetByRole(AriaRole.Navigation, new() { Name = "Main" })).ToBeVisibleAsync();
    }

    [Fact]
    public async Task On_a_phone_the_detailed_view_folds_its_sections_into_a_menu_and_nothing_scrolls_sideways()
    {
        await using var context = await browsers.NewPhoneAsync(app.BaseUrl);
        var page = await context.NewPageAsync();
        await page.GotoAsync("/account/login");
        await page.GetByLabel("Your email address").FillAsync(AppFactory.AdminEmail);
        await page.GetByRole(AriaRole.Button, new() { Name = "Email me a sign-in link" }).ClickAsync();
        await Expect(page.GetByRole(AriaRole.Heading, new() { Name = "Check your email" })).ToBeVisibleAsync();   // the email has been sent
        await page.GotoAsync(CapturingEmailSender.ExtractMagicLink(app.Email.To(AppFactory.AdminEmail).Last()).PathAndQuery);
        await page.GetByRole(AriaRole.Button, new() { Name = "Sign me in" }).ClickAsync();
        await page.GetByRole(AriaRole.Button, new() { Name = "Detailed" }).ClickAsync();

        var sections = page.GetByRole(AriaRole.Navigation, new() { Name = "Sections" });
        var people = sections.GetByRole(AriaRole.Link, new() { Name = "People and invitations" });
        await Expect(people).ToBeHiddenAsync();                                // folded away, so the page starts on the first screen
        await Expect(sections.GetByText("Menu")).ToBeVisibleAsync();

        foreach (var path in new[] { "/", "/admin", "/admin/users", "/admin/audit", "/admin/email/log", "/admin/features", "/library", "/status" })
        {
            await page.GotoAsync(path);
            await Accessibility.AssertNoHorizontalScrollAsync(page);
        }
        await Accessibility.AssertAccessibleAsync(page, "status page, detailed view on a phone");

        await sections.GetByText("Menu").TapAsync();
        await people.TapAsync();
        await Expect(page.GetByRole(AriaRole.Heading, new() { Name = "People" })).ToBeVisibleAsync();
        await Accessibility.AssertNoHorizontalScrollAsync(page);
    }
}
