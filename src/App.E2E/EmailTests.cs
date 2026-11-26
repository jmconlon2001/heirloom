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

/// <summary>Phase 5: the Exports page is usable and accessible.</summary>
public sealed class ExportPageTests(PostgresFixture postgres, BrowsersFixture browsers) : IAsyncLifetime
{
    private readonly KestrelAppFactory app = new(postgres);
    public ValueTask InitializeAsync() => app.InitializeAsync();
    public ValueTask DisposeAsync() => app.DisposeAsync();

    [Fact]
    public async Task IPad_admin_exports_page_is_accessible()
    {
        await using var context = await browsers.NewIPadAsync(app.BaseUrl);
        var page = await context.NewPageAsync();
        await page.GotoAsync("/account/login");
        await page.GetByLabel("Your email address").FillAsync(AppFactory.AdminEmail);
        await page.GetByRole(AriaRole.Button, new() { Name = "Email me a sign-in link" }).ClickAsync();
        await Expect(page.GetByRole(AriaRole.Heading, new() { Name = "Check your email" })).ToBeVisibleAsync();
        await page.GotoAsync(CapturingEmailSender.ExtractMagicLink(app.Email.To(AppFactory.AdminEmail).Last()).PathAndQuery);
        await page.GetByRole(AriaRole.Button, new() { Name = "Sign me in" }).ClickAsync();
        await Expect(page.GetByRole(AriaRole.Heading, new() { NameRegex = new System.Text.RegularExpressions.Regex("^Hello") })).ToBeVisibleAsync();   // signed in before moving on
        await page.GotoAsync("/admin/exports");
        await Expect(page.GetByRole(AriaRole.Heading, new() { Name = "Make a new export" })).ToBeVisibleAsync();
        await Accessibility.AssertAccessibleAsync(page, "exports page");
        await Accessibility.AssertNoHorizontalScrollAsync(page);
        // The app's own fonts are served locally.
        Assert.Contains("Atkinson Hyperlegible", await page.EvaluateAsync<string>("getComputedStyle(document.body).fontFamily"));
    }
}

/// <summary>The public landing page looks right and is accessible on an iPad and at A++.</summary>
public sealed class LandingTests(PostgresFixture postgres, BrowsersFixture browsers) : IAsyncLifetime
{
    private readonly KestrelAppFactory app = new(postgres);
    public ValueTask InitializeAsync() => app.InitializeAsync();
    public ValueTask DisposeAsync() => app.DisposeAsync();

    [Fact]
    public async Task IPad_landing_page_is_inviting_and_accessible()
    {
        await using var context = await browsers.NewIPadAsync(app.BaseUrl);
        var page = await context.NewPageAsync();
        await page.GotoAsync("/");
        await Expect(page.GetByRole(AriaRole.Heading, new() { Name = "Stories worth handing down." })).ToBeVisibleAsync();
        await Accessibility.ScreenshotAsync(page, "ipad-landing");
        await Accessibility.AssertAccessibleAsync(page, "landing page");
        await Accessibility.AssertTapTargetsAsync(page);
        await page.GetByRole(AriaRole.Button, new() { Name = "A++" }).ClickAsync();
        await Accessibility.AssertNoHorizontalScrollAsync(page);
        await Accessibility.AssertAccessibleAsync(page, "landing page at A++");
        await page.GetByRole(AriaRole.Link, new() { Name = "Sign in" }).First.ClickAsync();
        await Expect(page.GetByRole(AriaRole.Heading, new() { Name = "Sign in" })).ToBeVisibleAsync();
    }
}

/// <summary>The spreadsheet imports: the upload page and the check-before-adding table are usable and accessible on an iPad.</summary>
public sealed class ImportPageTests(PostgresFixture postgres, BrowsersFixture browsers) : IAsyncLifetime
{
    private readonly KestrelAppFactory app = new(postgres);
    public ValueTask InitializeAsync() => app.InitializeAsync();
    public ValueTask DisposeAsync() => app.DisposeAsync();

    [Fact]
    public async Task IPad_admin_imports_people_and_stories_from_the_templates()
    {
        await using var context = await browsers.NewIPadAsync(app.BaseUrl);
        var page = await context.NewPageAsync();
        await page.GotoAsync("/account/login");
        await page.GetByLabel("Your email address").FillAsync(AppFactory.AdminEmail);
        await page.GetByRole(AriaRole.Button, new() { Name = "Email me a sign-in link" }).ClickAsync();
        await Expect(page.GetByRole(AriaRole.Heading, new() { Name = "Check your email" })).ToBeVisibleAsync();
        await page.GotoAsync(CapturingEmailSender.ExtractMagicLink(app.Email.To(AppFactory.AdminEmail).Last()).PathAndQuery);
        await page.GetByRole(AriaRole.Button, new() { Name = "Sign me in" }).ClickAsync();
        await Expect(page.GetByRole(AriaRole.Heading, new() { NameRegex = new Regex("^Hello") })).ToBeVisibleAsync();

        await page.GotoAsync("/admin/import/people");
        await Accessibility.AssertAccessibleAsync(page, "people import page");
        await page.GetByLabel("Choose a .csv file").SetInputFilesAsync(new FilePayload
        {
            Name = "people.csv", MimeType = "text/csv", Buffer = System.Text.Encoding.UTF8.GetBytes(FamilyMemories.Imports.PeopleCsvImporter.Template()),
        });
        await page.GetByRole(AriaRole.Button, new() { Name = "Show me what's in it" }).ClickAsync();
        await Expect(page.GetByRole(AriaRole.Heading, new() { Name = "Check before adding" })).ToBeVisibleAsync();
        await Accessibility.AssertAccessibleAsync(page, "people import preview");
        await Accessibility.AssertTapTargetsAsync(page);
        await page.GetByRole(AriaRole.Button, new() { Name = "Add these people" }).ClickAsync();
        await Expect(page.GetByText("3 new people added to the family tree")).ToBeVisibleAsync();
        await Accessibility.AssertAccessibleAsync(page, "people import result");

        await page.GotoAsync("/admin/import/stories");
        await page.GetByLabel("Choose a .csv file").SetInputFilesAsync(new FilePayload
        {
            Name = "stories.csv", MimeType = "text/csv", Buffer = System.Text.Encoding.UTF8.GetBytes(FamilyMemories.Imports.StoriesCsvImporter.Template()),
        });
        await page.GetByRole(AriaRole.Button, new() { Name = "Show me what's in it" }).ClickAsync();
        await Expect(page.GetByText("2 stories found; 2 ready to add")).ToBeVisibleAsync();
        await Accessibility.AssertAccessibleAsync(page, "stories import preview");
        await page.GetByRole(AriaRole.Button, new() { Name = "Add these stories" }).ClickAsync();
        await Expect(page.GetByText("2 stories added")).ToBeVisibleAsync();
    }
}

/// <summary>Site settings: the page is accessible, and a new device starts with the family's chosen display.</summary>
public sealed class SiteSettingsPageTests(PostgresFixture postgres, BrowsersFixture browsers) : IAsyncLifetime
{
    private readonly KestrelAppFactory app = new(postgres);
    public ValueTask InitializeAsync() => app.InitializeAsync();
    public ValueTask DisposeAsync() => app.DisposeAsync();

    [Fact]
    public async Task IPad_admin_sets_help_phone_and_a_larger_starting_text_size()
    {
        await using var context = await browsers.NewIPadAsync(app.BaseUrl);
        var page = await context.NewPageAsync();
        await page.GotoAsync("/account/login");
        await page.GetByLabel("Your email address").FillAsync(AppFactory.AdminEmail);
        await page.GetByRole(AriaRole.Button, new() { Name = "Email me a sign-in link" }).ClickAsync();
        await Expect(page.GetByRole(AriaRole.Heading, new() { Name = "Check your email" })).ToBeVisibleAsync();
        await page.GotoAsync(CapturingEmailSender.ExtractMagicLink(app.Email.To(AppFactory.AdminEmail).Last()).PathAndQuery);
        await page.GetByRole(AriaRole.Button, new() { Name = "Sign me in" }).ClickAsync();
        await Expect(page.GetByRole(AriaRole.Heading, new() { NameRegex = new Regex("^Hello") })).ToBeVisibleAsync();

        await page.GotoAsync("/admin/site");
        await Accessibility.AssertAccessibleAsync(page, "site settings page");
        await Accessibility.AssertTapTargetsAsync(page);
        await page.GetByLabel("Their name").FillAsync("Aunt Ann");
        await page.GetByLabel("Their phone number").FillAsync("(555) 010-0144");
        await page.GetByLabel("A+ Larger text").CheckAsync();
        await page.GetByRole(AriaRole.Button, new() { Name = "Save" }).ClickAsync();
        await Expect(page.GetByText("Saved. Every page")).ToBeVisibleAsync();
        await Expect(page.Locator(".need-help")).ToContainTextAsync("Call Aunt Ann at (555) 010-0144");

        // A brand-new device starts at A+, and the buttons carry on from there.
        await using var fresh = await browsers.NewIPadAsync(app.BaseUrl);
        var visitor = await fresh.NewPageAsync();
        await visitor.GotoAsync("/");
        await Expect(visitor.Locator("html")).ToHaveClassAsync(new Regex(@"\btext-1\b"));
        await visitor.GetByRole(AriaRole.Button, new() { Name = "High contrast" }).ClickAsync();
        await Expect(visitor.Locator("html")).ToHaveClassAsync(new Regex(@"\btext-1\b.*high-contrast"));
    }
}

/// <summary>A person's life events and sources from the main tree are readable and accessible on an iPad.</summary>
public sealed class LifeEventsPageTests(PostgresFixture postgres, BrowsersFixture browsers) : IAsyncLifetime
{
    private readonly KestrelAppFactory app = new(postgres);
    public ValueTask InitializeAsync() => app.InitializeAsync();
    public ValueTask DisposeAsync() => app.DisposeAsync();

    private const string Tree = """
        0 HEAD
        0 @I1@ INDI
        1 NAME William /Conlon/
        1 BIRT
        2 DATE 14 FEB 1898
        2 PLAC Cork, Ireland
        2 SOUR @S1@
        3 PAGE Entry 112
        1 IMMI
        2 DATE 1920
        2 PLAC Ellis Island, New York
        2 NOTE Arrived on the S.S. Celtic.
        1 _FSFTID LZ7Q-ABC
        0 @S1@ SOUR
        1 TITL Parish register
        0 TRLR
        """;

    [Fact]
    public async Task IPad_admin_imports_the_main_tree_and_reads_a_persons_sources()
    {
        await using var context = await browsers.NewIPadAsync(app.BaseUrl);
        var page = await context.NewPageAsync();
        await page.GotoAsync("/account/login");
        await page.GetByLabel("Your email address").FillAsync(AppFactory.AdminEmail);
        await page.GetByRole(AriaRole.Button, new() { Name = "Email me a sign-in link" }).ClickAsync();
        await Expect(page.GetByRole(AriaRole.Heading, new() { Name = "Check your email" })).ToBeVisibleAsync();
        await page.GotoAsync(CapturingEmailSender.ExtractMagicLink(app.Email.To(AppFactory.AdminEmail).Last()).PathAndQuery);
        await page.GetByRole(AriaRole.Button, new() { Name = "Sign me in" }).ClickAsync();
        await Expect(page.GetByRole(AriaRole.Heading, new() { NameRegex = new Regex("^Hello") })).ToBeVisibleAsync();

        await page.GotoAsync("/admin/gedcom");
        await page.GetByLabel("Choose a .ged file").SetInputFilesAsync(new FilePayload { Name = "tree.ged", MimeType = "text/plain", Buffer = System.Text.Encoding.UTF8.GetBytes(Tree) });
        await page.GetByRole(AriaRole.Button, new() { Name = "Show me what's in it" }).ClickAsync();
        await page.GetByLabel(new Regex("This file is our main family tree")).CheckAsync();
        await Accessibility.AssertAccessibleAsync(page, "GEDCOM preview");
        await page.GetByRole(AriaRole.Button, new() { Name = "Import" }).ClickAsync();

        await page.GotoAsync("/people");
        await page.GetByRole(AriaRole.Link, new() { Name = "William Conlon" }).First.ClickAsync();
        await page.GetByText("Life events and sources (1)").ClickAsync();
        await Expect(page.GetByText("Arrived on the S.S. Celtic.")).ToBeVisibleAsync();
        await page.GetByText("Sources (1) for Birth").ClickAsync();
        await Expect(page.GetByText("Parish register")).ToBeVisibleAsync();
        await Expect(page.GetByRole(AriaRole.Link, new() { Name = "FamilySearch" })).ToHaveAttributeAsync("href", "https://www.familysearch.org/tree/person/details/LZ7Q-ABC");
        await Accessibility.AssertAccessibleAsync(page, "person page with life events");
        await Accessibility.AssertTapTargetsAsync(page);
        await Accessibility.AssertNoHorizontalScrollAsync(page);
    }
}

/// <summary>The family archive on an iPad: a folder, a letter with its scan and transcription, all accessible (D-080).</summary>
public sealed class ArchivePageTests(PostgresFixture postgres, BrowsersFixture browsers) : IAsyncLifetime
{
    private readonly KestrelAppFactory app = new(postgres);
    public ValueTask InitializeAsync() => app.InitializeAsync();
    public ValueTask DisposeAsync() => app.DisposeAsync();

    [Fact]
    public async Task IPad_admin_files_a_letter_in_a_folder_and_the_family_reads_it()
    {
        await using var context = await browsers.NewIPadAsync(app.BaseUrl);
        var page = await context.NewPageAsync();
        await page.GotoAsync("/account/login");
        await page.GetByLabel("Your email address").FillAsync(AppFactory.AdminEmail);
        await page.GetByRole(AriaRole.Button, new() { Name = "Email me a sign-in link" }).ClickAsync();
        await Expect(page.GetByRole(AriaRole.Heading, new() { Name = "Check your email" })).ToBeVisibleAsync();
        await page.GotoAsync(CapturingEmailSender.ExtractMagicLink(app.Email.To(AppFactory.AdminEmail).Last()).PathAndQuery);
        await page.GetByRole(AriaRole.Button, new() { Name = "Sign me in" }).ClickAsync();
        await Expect(page.GetByRole(AriaRole.Heading, new() { NameRegex = new Regex("^Hello") })).ToBeVisibleAsync();

        await page.GotoAsync("/library");
        await page.GetByRole(AriaRole.Navigation, new() { Name = "Family Library" }).GetByRole(AriaRole.Link, new() { Name = "Archive" }).ClickAsync();
        await Expect(page.GetByRole(AriaRole.Heading, new() { Name = "Family archive", Exact = true })).ToBeVisibleAsync();
        await Accessibility.AssertAccessibleAsync(page, "empty archive");
        await page.GetByText("New folder").ClickAsync();
        await page.GetByLabel("Name").FillAsync("Letters");
        await page.GetByRole(AriaRole.Button, new() { Name = "Make the folder" }).ClickAsync();
        await Expect(page.GetByRole(AriaRole.Heading, new() { Name = "Letters" })).ToBeVisibleAsync();

        await page.GetByRole(AriaRole.Link, new() { Name = "Add to the archive" }).ClickAsync();
        await Accessibility.AssertAccessibleAsync(page, "add to the archive");
        await Accessibility.AssertTapTargetsAsync(page);
        await page.Locator("#Files").SetInputFilesAsync(AppFactory.Fixture("photo.jpg"));
        await page.GetByLabel("Title").FillAsync("Letter from Joe in France");
        await page.GetByLabel(new Regex("^When")).FillAsync("about 1944");
        await page.GetByLabel(new Regex("^What it says")).FillAsync("Dear Rose,\nThe weather here is fine.\nLove, Joe");
        await page.GetByLabel(new Regex("^Where the original is now")).FillAsync("Aunt Ann's cedar chest");
        await page.GetByRole(AriaRole.Button, new() { Name = "Add to the archive" }).ClickAsync();

        await Expect(page.GetByRole(AriaRole.Heading, new() { Name = "Letter from Joe in France" })).ToBeVisibleAsync();
        await Expect(page.GetByText("around 1944")).ToBeVisibleAsync();
        await Expect(page.Locator(".archive-file img")).ToHaveCountAsync(1);
        await Expect(page.Locator(".transcription-text")).ToContainTextAsync("The weather here is fine.");
        await Accessibility.AssertAccessibleAsync(page, "archive item");
        await Accessibility.AssertTapTargetsAsync(page);
        await Accessibility.AssertNoHorizontalScrollAsync(page);

        await page.GetByRole(AriaRole.Link, new() { Name = "Letters" }).ClickAsync();
        await Expect(page.Locator(".archive-card")).ToHaveCountAsync(1);
        await Accessibility.AssertAccessibleAsync(page, "archive folder");
    }
}
