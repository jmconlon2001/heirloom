using System.Text.RegularExpressions;
using FamilyMemories.Tests.Infrastructure;
using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;

namespace FamilyMemories.E2E;

/// <summary>The status page and the Basic / Complex admin views, on an iPad.</summary>
public sealed class AdminViewTests(PostgresFixture postgres, BrowsersFixture browsers) : IAsyncLifetime
{
    private readonly KestrelAppFactory app = new(postgres);
    public ValueTask InitializeAsync() => app.InitializeAsync();
    public ValueTask DisposeAsync() => app.DisposeAsync();

    [Fact]
    public async Task Status_page_and_both_admin_views_are_accessible_and_the_choice_sticks()
    {
        await using var context = await browsers.NewIPadAsync(app.BaseUrl);
        var page = await context.NewPageAsync();

        await page.GotoAsync("/status");
        await Expect(page.GetByRole(AriaRole.Heading, new() { Name = "Is everything working?" })).ToBeVisibleAsync();
        await Accessibility.AssertAccessibleAsync(page, "public status page");

        await page.GotoAsync("/account/login");
        await page.GetByLabel("Your email address").FillAsync(AppFactory.AdminEmail);
        await page.GetByRole(AriaRole.Button, new() { Name = "Email me a sign-in link" }).ClickAsync();
        await page.GotoAsync(CapturingEmailSender.ExtractMagicLink(app.Email.To(AppFactory.AdminEmail).Last()).PathAndQuery);
        await page.GetByRole(AriaRole.Button, new() { Name = "Sign me in" }).ClickAsync();
        await Expect(page.GetByRole(AriaRole.Heading, new() { NameRegex = new Regex("^Hello") })).ToBeVisibleAsync();

        await page.GotoAsync("/admin");
        await Expect(page.GetByRole(AriaRole.Button, new() { Name = "Basic" })).ToHaveAttributeAsync("aria-pressed", "true");
        await Accessibility.AssertAccessibleAsync(page, "admin home, basic view");

        await page.GetByRole(AriaRole.Button, new() { Name = "Complex" }).ClickAsync();
        await Expect(page.GetByRole(AriaRole.Navigation, new() { Name = "Family admin" })).ToBeVisibleAsync();
        await Expect(page.GetByText("finished stories")).ToBeVisibleAsync();
        await Accessibility.AssertAccessibleAsync(page, "admin home, complex view");
        await Accessibility.AssertNoHorizontalScrollAsync(page);

        await page.GetByRole(AriaRole.Navigation, new() { Name = "Family admin" }).GetByRole(AriaRole.Link, new() { Name = "People and invitations" }).ClickAsync();
        await Expect(page.GetByRole(AriaRole.Heading, new() { Name = "People" })).ToBeVisibleAsync();
        await Accessibility.AssertAccessibleAsync(page, "people, complex view");

        await page.GotoAsync("/status");
        await Expect(page.GetByText("For the technically minded")).ToBeVisibleAsync();
        await Accessibility.AssertAccessibleAsync(page, "status page, complex view");

        // Remembered after a reload; back to Basic with one tap.
        await page.GotoAsync("/admin");
        await Expect(page.GetByRole(AriaRole.Button, new() { Name = "Complex" })).ToHaveAttributeAsync("aria-pressed", "true");
        await page.GetByRole(AriaRole.Button, new() { Name = "Basic" }).ClickAsync();
        await Expect(page.GetByRole(AriaRole.Navigation, new() { Name = "Family admin" })).ToHaveCountAsync(0);
    }
}
