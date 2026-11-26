using System.Text.RegularExpressions;
using FamilyMemories.Data;
using FamilyMemories.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;

namespace FamilyMemories.E2E;

/// <summary>Phase 1 acceptance: on an iPad, a storyteller answers a question with words, a photo and their voice.</summary>
public sealed class AnswerTests(PostgresFixture postgres, BrowsersFixture browsers) : IAsyncLifetime
{
    private readonly KestrelAppFactory app = new(postgres);

    public ValueTask InitializeAsync() => app.InitializeAsync();
    public ValueTask DisposeAsync() => app.DisposeAsync();

    private async Task<IPage> SignedInIPadAsync(string email)
    {
        var context = await browsers.NewIPadAsync(app.BaseUrl, microphone: true);
        var page = await context.NewPageAsync();
        await page.GotoAsync("/account/login");
        await page.GetByLabel("Your email address").FillAsync(email);
        await page.GetByRole(AriaRole.Button, new() { Name = "Email me a sign-in link" }).ClickAsync();
        await Expect(page.GetByRole(AriaRole.Heading, new() { Name = "Check your email" })).ToBeVisibleAsync();
        var link = CapturingEmailSender.ExtractMagicLink(app.Email.To(email).Last());
        await page.GotoAsync(link.PathAndQuery);
        await page.GetByRole(AriaRole.Button, new() { Name = "Sign me in" }).ClickAsync();
        await Expect(page.GetByRole(AriaRole.Heading, new() { NameRegex = new Regex("^Hello") })).ToBeVisibleAsync();
        return page;
    }

    [Fact]
    public async Task IPad_storyteller_answers_with_text_a_photo_and_a_voice_clip()
    {
        var rose = await app.CreateUserAsync("Rose Smith", "rose-e2e@example.test", [Roles.Storyteller, Roles.Family], PromptSetKind.Core, Pacing.Weekly);
        var page = await SignedInIPadAsync("rose-e2e@example.test");
        await Accessibility.AssertAccessibleAsync(page, "storyteller home");

        // Home -> the one question for this week.
        await page.GetByRole(AriaRole.Link, new() { Name = "Answer this question" }).ClickAsync();
        await Expect(page.GetByRole(AriaRole.Heading, new() { Name = "Where and when were you born? Do you know how your name was chosen?" })).ToBeVisibleAsync();
        await Accessibility.AssertAccessibleAsync(page, "empty writing page");
        await Accessibility.AssertTapTargetsAsync(page);

        // Words: autosave kicks in without pressing anything.
        var story = page.GetByLabel("Your story");
        await story.FillAsync("I was born in 1931 in a farmhouse outside Akron.\n\nMy mother named me after her favorite rose bush.");
        await Expect(page.Locator("[data-save-status]")).ToContainTextAsync("Saved ✓", new() { Timeout = 15_000 });

        // A photo, through the big "Add photos" button's file picker.
        await page.Locator("#Files").SetInputFilesAsync(AppFactory.Fixture("photo.jpg"));
        await Expect(page.Locator(".attachment img")).ToHaveCountAsync(1, new() { Timeout = 30_000 });
        await page.GetByLabel("Caption").First.FillAsync("The farmhouse, 1935");

        // A voice clip: Record, talk for a moment, Stop.
        await page.GetByRole(AriaRole.Button, new() { Name = "Record my voice" }).ClickAsync();
        await Expect(page.Locator(".recording-time")).ToBeVisibleAsync();
        await page.WaitForTimeoutAsync(2500);
        await page.GetByRole(AriaRole.Button, new() { Name = "Stop recording" }).ClickAsync();
        await Expect(page.Locator(".attachment audio")).ToHaveCountAsync(1, new() { Timeout = 30_000 });

        // Keep writing after adding things: autosave must carry on (no false "changed elsewhere" warning).
        await story.PressSequentiallyAsync(" She grew roses all her life.");
        await story.BlurAsync();
        await Expect(page.Locator("[data-save-status]")).ToContainTextAsync("Saved ✓", new() { Timeout = 15_000 });
        await Expect(page.Locator("[data-save-status]")).Not.ToContainTextAsync("changed in another window");

        await Accessibility.ScreenshotAsync(page, "ipad-answer-filled");
        await Accessibility.AssertAccessibleAsync(page, "writing page with a photo and a recording");

        // Finish.
        await page.GetByRole(AriaRole.Button, new() { Name = "I'm finished with this story" }).ClickAsync();
        await Expect(page.GetByRole(AriaRole.Heading, new() { Name = "Thank you" })).ToBeVisibleAsync();
        await Expect(page.GetByText("You've shared 1 story")).ToBeVisibleAsync();
        await Accessibility.AssertAccessibleAsync(page, "thank-you page");

        var memory = await app.WithDbAsync(db => db.Memories.Include(m => m.Attachments).SingleAsync(m => m.StorytellerId == rose.Id));
        Assert.Equal(MemoryStatus.Complete, memory.Status);
        Assert.StartsWith("I was born in 1931", memory.Body);
        Assert.Contains("\n\nMy mother named me", memory.Body);
        Assert.EndsWith("She grew roses all her life.", memory.Body);
        var photo = Assert.Single(memory.Attachments, a => a.Kind == AttachmentKind.Photo);
        Assert.Equal("The farmhouse, 1935", photo.Caption);
        var voice = Assert.Single(memory.Attachments, a => a.Kind == AttachmentKind.Audio);
        Assert.True(voice.IsVoiceRecording);
        Assert.Equal(ProcessingState.Ready, voice.State);
        Assert.True(voice.DurationSeconds > 1);

        // Reading it back.
        await page.GetByRole(AriaRole.Link, new() { Name = "Read the story" }).ClickAsync();
        await Expect(page.GetByText("Told by Rose Smith")).ToBeVisibleAsync();
        await Expect(page.Locator("figcaption")).ToHaveTextAsync("The farmhouse, 1935");
        await Accessibility.AssertAccessibleAsync(page, "reading the story");
    }

    [Fact]
    public async Task A_recording_that_could_not_be_sent_is_kept_on_the_device_and_added_next_time()
    {
        var rose = await app.CreateUserAsync("Rose Smith", "rose-keep@example.test", [Roles.Storyteller], PromptSetKind.Core, Pacing.Weekly);
        var page = await SignedInIPadAsync("rose-keep@example.test");
        await page.GetByRole(AriaRole.Link, new() { Name = "Answer this question" }).ClickAsync();
        var writeUrl = page.Url;

        // The Wi-Fi drops just as she stops recording: the upload can't start.
        await page.RouteAsync("**/api/memories/*/uploads", route => route.AbortAsync());
        await page.GetByRole(AriaRole.Button, new() { Name = "Record my voice" }).ClickAsync();
        await Expect(page.Locator(".recording-time")).ToBeVisibleAsync();
        await page.WaitForTimeoutAsync(2500);
        await page.GetByRole(AriaRole.Button, new() { Name = "Stop recording" }).ClickAsync();
        await Expect(page.Locator("[data-save-status]")).ToContainTextAsync("kept on this device", new() { Timeout = 15_000 });
        Assert.False(await app.WithDbAsync(db => db.Attachments.AnyAsync(a => a.Memory!.StorytellerId == rose.Id)));

        // She comes back later (connection fine again): the recording is added without her doing anything.
        await page.UnrouteAllAsync();
        await page.GotoAsync("/");
        await page.GotoAsync(writeUrl);
        await Expect(page.Locator("[data-save-status]")).ToContainTextAsync("hadn't finished saving");
        await Expect(page.Locator(".attachment audio")).ToHaveCountAsync(1, new() { Timeout = 30_000 });
        var voice = await app.WithDbAsync(db => db.Attachments.SingleAsync(a => a.Memory!.StorytellerId == rose.Id));
        Assert.True(voice.IsVoiceRecording);
        Assert.True(voice.DurationSeconds > 1);

        // Only once: the device copy is gone after it reached the server.
        await page.ReloadAsync();
        await Expect(page.Locator(".attachment audio")).ToHaveCountAsync(1);
        await page.WaitForTimeoutAsync(1500);
        Assert.Equal(1, await app.WithDbAsync(db => db.Attachments.CountAsync(a => a.Memory!.StorytellerId == rose.Id)));
    }

    [Fact]
    public async Task Photos_can_be_put_in_order_by_dragging_or_with_the_arrow_keys()
    {
        var rose = await app.CreateUserAsync("Rose Smith", "rose-drag@example.test", [Roles.Storyteller], PromptSetKind.Core, Pacing.Weekly);
        var page = await SignedInIPadAsync("rose-drag@example.test");
        await page.GetByRole(AriaRole.Link, new() { Name = "Answer this question" }).ClickAsync();
        await page.Locator("#Files").SetInputFilesAsync(AppFactory.Fixture("photo.jpg"));
        await Expect(page.Locator(".attachment img")).ToHaveCountAsync(1, new() { Timeout = 30_000 });
        await page.Locator("#Files").SetInputFilesAsync(AppFactory.Fixture("photo.heic"));
        await Expect(page.Locator(".attachment img")).ToHaveCountAsync(2, new() { Timeout = 30_000 });

        async Task<List<string>> Order() => await app.WithDbAsync(db => db.Attachments
            .Where(a => a.Memory!.StorytellerId == rose.Id && a.RemovedAt == null).OrderBy(a => a.SortOrder).Select(a => a.OriginalFileName).ToListAsync());
        Assert.Equal(["photo.jpg", "photo.heic"], await Order());

        // Keyboard: focus the second item's handle and press the up arrow.
        var handles = page.GetByRole(AriaRole.Button, new() { Name = "Drag to move, or use the arrow keys" });
        await Expect(handles).ToHaveCountAsync(2);
        await handles.Nth(1).FocusAsync();
        await page.Keyboard.PressAsync("ArrowUp");
        await Expect(page.Locator("[data-save-status]")).ToContainTextAsync("New order saved");
        await Expect(page.Locator(".attachment").Nth(0).Locator("[data-drag-handle]")).ToBeFocusedAsync();   // list redrawn, focus kept
        Assert.Equal(["photo.heic", "photo.jpg"], await Order());

        // Dragging: the first item's handle down past the second.
        var first = page.Locator(".attachment").Nth(0).Locator("[data-drag-handle]");
        var second = page.Locator(".attachment").Nth(1);
        await first.ScrollIntoViewIfNeededAsync();
        var from = (await first.BoundingBoxAsync())!;
        var to = (await second.BoundingBoxAsync())!;
        await page.Mouse.MoveAsync(from.X + from.Width / 2, from.Y + from.Height / 2);
        await page.Mouse.DownAsync();
        await page.Mouse.MoveAsync(from.X + from.Width / 2, to.Y + to.Height - 10, new() { Steps = 10 });
        await page.Mouse.UpAsync();
        await Expect(page.Locator(".attachment").Nth(0).Locator("img")).ToHaveAttributeAsync("alt", "photo.jpg");
        for (var i = 0; i < 50 && (await Order())[0] != "photo.jpg"; i++) await Task.Delay(100);
        Assert.Equal(["photo.jpg", "photo.heic"], await Order());
        await Accessibility.AssertAccessibleAsync(page, "writing page with drag handles");
    }

    [Fact]
    public async Task IPad_writing_page_at_A_plus_plus_is_usable_and_skip_is_gentle()
    {
        await app.CreateUserAsync("Rose Smith", "rose-a3@example.test", [Roles.Storyteller], PromptSetKind.Core, Pacing.AllAtOnce);
        var page = await SignedInIPadAsync("rose-a3@example.test");
        await page.GetByRole(AriaRole.Button, new() { Name = "A++" }).ClickAsync();
        await page.GetByRole(AriaRole.Link, new() { Name = "Answer this question" }).ClickAsync();

        await page.GetByText("Add when and where (optional)").ClickAsync();
        await page.GetByLabel("Year", new() { Exact = true }).FillAsync("1931");
        await page.GetByLabel("I'm not sure of the exact year").CheckAsync();

        await Accessibility.ScreenshotAsync(page, "ipad-answer-a++");
        await Accessibility.AssertAccessibleAsync(page, "writing page at A++");
        await Accessibility.AssertNoHorizontalScrollAsync(page);
        await Accessibility.AssertTapTargetsAsync(page);

        await page.GetByRole(AriaRole.Button, new() { Name = "Skip for now" }).ClickAsync();
        await Expect(page.GetByText("We've saved this question for later")).ToBeVisibleAsync();
        await Expect(page.GetByRole(AriaRole.Heading, new() { NameRegex = new Regex("Describe the home you grew up in") })).ToBeVisibleAsync();
    }

    [Fact]
    public async Task Words_typed_after_the_session_ends_come_back_after_signing_in()
    {
        await app.CreateUserAsync("Rose Smith", "rose-expire@example.test", [Roles.Storyteller], PromptSetKind.Core, Pacing.AllAtOnce);
        var page = await SignedInIPadAsync("rose-expire@example.test");
        await page.GetByRole(AriaRole.Link, new() { Name = "Answer this question" }).ClickAsync();
        var writeUrl = page.Url;

        // The session ends (e.g. an admin signed this iPad out) while the page is open.
        await page.Context.ClearCookiesAsync(new() { Name = "fm_auth" });
        await page.GetByLabel("Your story").FillAsync("Words I don't want to lose.");
        await page.GetByLabel("Your story").BlurAsync();
        await Expect(page.Locator("[data-save-status]")).ToContainTextAsync("your words are safe on this device", new() { Timeout = 15_000 });

        // Sign in again; the page puts the words back and saves them.
        await page.GetByRole(AriaRole.Link, new() { Name = "Sign in again" }).ClickAsync();
        await page.GetByLabel("Your email address").FillAsync("rose-expire@example.test");
        await page.GetByRole(AriaRole.Button, new() { Name = "Email me a sign-in link" }).ClickAsync();
        var link = CapturingEmailSender.ExtractMagicLink(app.Email.To("rose-expire@example.test").Last());
        await page.GotoAsync(link.PathAndQuery);
        await page.GetByRole(AriaRole.Button, new() { Name = "Sign me in" }).ClickAsync();
        await Expect(page).ToHaveURLAsync(writeUrl);
        await Expect(page.GetByLabel("Your story")).ToHaveValueAsync("Words I don't want to lose.");
        await Expect(page.Locator("[data-save-status]")).ToContainTextAsync("Saved ✓", new() { Timeout = 15_000 });

        Assert.Equal("Words I don't want to lose.",
            await app.WithDbAsync(db => db.Memories.Where(m => m.Storyteller!.Email == "rose-expire@example.test").Select(m => m.Body).SingleAsync()));
    }
}
