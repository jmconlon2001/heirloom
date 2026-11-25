using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FamilyMemories.Data;
using FamilyMemories.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace FamilyMemories.Tests;

public sealed class StoryPageTests(PostgresFixture postgres) : IAsyncLifetime
{
    private readonly AppFactory app = new(postgres);
    public ValueTask InitializeAsync() => app.InitializeAsync();
    public ValueTask DisposeAsync() => app.DisposeAsync();

    private static string Decode(string html) => WebUtility.HtmlDecode(html);

    private static MultipartFormDataContent Form(string pageHtml, params (string Key, string Value)[] fields)
    {
        var form = new MultipartFormDataContent { { new StringContent(HtmlForms.AntiforgeryToken(pageHtml)), "__RequestVerificationToken" } };
        foreach (var (k, v) in fields) form.Add(new StringContent(v), k);
        return form;
    }

    [Fact]
    public async Task Home_shows_the_next_question_and_gentle_progress()
    {
        await app.CreateUserAsync("Rose Smith", "home@example.test", [Roles.Storyteller], PromptSetKind.Core, Pacing.Weekly);
        var browser = await Browser.SignedInAsync(app, "home@example.test");
        var html = Decode(await browser.Http.GetStringAsync("/"));

        Assert.Contains("Hello, Rose", html);
        Assert.Contains("Your next question", html);
        Assert.Contains("Where and when were you born?", html);
        Assert.Contains("Answer this question", html);
        Assert.Contains("Roots & Early Years", html);
        Assert.DoesNotContain("streak", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Family admin", html);
    }

    [Fact]
    public async Task The_writing_page_has_one_question_and_everything_needed()
    {
        var rose = await app.CreateUserAsync("Rose", "page@example.test", [Roles.Storyteller], PromptSetKind.Core);
        var browser = await Browser.SignedInAsync(app, "page@example.test");
        var (_, _, raw) = await StoryClient.OpenFirstQuestionAsync(browser, app, rose.Id);
        var html = Decode(raw);

        Assert.Contains("<h1 id=\"question\" class=\"prompt-question\">Where and when were you born?", html);
        Assert.Contains("Were you named after anyone in the family?", html);          // a hint
        Assert.Contains("A baby photo or a birth announcement", html);                // suggested attachment
        Assert.Contains("Record my voice", html);
        Assert.Contains("Add photos", html);
        Assert.Contains(">Save</button>", html);
        Assert.Contains("Skip for now", html);
        Assert.Contains("I'd rather not answer", html);
        Assert.Contains("tap the microphone on your keyboard", html);
        Assert.Contains("spellcheck=\"true\"", html);
    }

    [Fact]
    public async Task Without_javascript_save_keeps_words_date_and_a_photo_then_finish_completes()
    {
        var rose = await app.CreateUserAsync("Rose", "nojs@example.test", [Roles.Storyteller], PromptSetKind.Core);
        var browser = await Browser.SignedInAsync(app, "nojs@example.test");
        var (memoryId, _, page) = await StoryClient.OpenFirstQuestionAsync(browser, app, rose.Id);

        var form = Form(page, ("Body", "I was born on a farm.\n\nIt was cold."), ("WhenYear", "1940"), ("WhenCirca", "true"), ("Place", "Akron, Ohio"));
        var photo = new ByteArrayContent(await File.ReadAllBytesAsync(AppFactory.Fixture("photo.jpg")));
        form.Add(photo, "Files", "farm.jpg");
        var save = await browser.Http.PostAsync($"/stories/{memoryId}/write?handler=Save", form);
        Assert.Equal(HttpStatusCode.Redirect, save.StatusCode);

        var memory = await app.WithDbAsync(db => db.Memories.Include(m => m.Attachments).SingleAsync(m => m.Id == memoryId));
        Assert.Equal("I was born on a farm.\n\nIt was cold.", memory.Body);
        Assert.Equal("around 1940", memory.When.Format());
        Assert.Equal("Akron, Ohio", memory.Place);
        Assert.Equal(MemoryStatus.Draft, memory.Status);
        Assert.Equal(ProcessingState.Ready, Assert.Single(memory.Attachments).State);

        var after = Decode(await browser.Http.GetStringAsync($"/stories/{memoryId}/write"));
        Assert.Contains("Saved at", after);
        Assert.Contains("I was born on a farm.", after);

        var finish = await browser.Http.PostAsync($"/stories/{memoryId}/write?handler=Finish", Form(after,
            ("Body", memory.Body), ("WhenYear", "1940"), ("WhenCirca", "true"), ("Place", "Akron, Ohio")));
        Assert.Equal($"/stories/{memoryId}/done", finish.Headers.Location?.OriginalString);
        var done = Decode(await browser.Http.GetStringAsync($"/stories/{memoryId}/done"));
        Assert.Contains("Thank you", done);
        Assert.Contains("You've shared <strong>1 story</strong>", done);
        Assert.Contains("Answer this question", done); // the next one, no pressure

        var read = Decode(await browser.Http.GetStringAsync($"/stories/{memoryId}"));
        Assert.Contains("Told by Rose", read);
        Assert.Contains("around 1940 · Akron, Ohio", read);
        Assert.Contains("<p>I was born on a farm.</p>", read);
        Assert.Contains("<p>It was cold.</p>", read);
    }

    [Fact]
    public async Task Skip_moves_on_and_decline_is_respected()
    {
        var rose = await app.CreateUserAsync("Rose", "skip@example.test", [Roles.Storyteller], PromptSetKind.Core);
        var browser = await Browser.SignedInAsync(app, "skip@example.test");
        var (memoryId, _, page) = await StoryClient.OpenFirstQuestionAsync(browser, app, rose.Id);

        var skip = await browser.Http.PostAsync($"/stories/{memoryId}/write?handler=Skip", Form(page));
        var secondPrompt = await app.WithDbAsync(db => db.Prompts.Where(p => p.Key == "roots-home").Select(p => p.Id).SingleAsync());
        Assert.Equal($"/answer/{secondPrompt}", skip.Headers.Location?.OriginalString);

        var (path, second, _) = await browser.GetAsync($"/answer/{secondPrompt}");
        Assert.Contains("We've saved this question for later", Decode(second));
        var secondId = Guid.Parse(path.Split('/')[2]);
        await browser.Http.PostAsync($"/stories/{secondId}/write?handler=Decline", Form(second));

        var statuses = await app.WithDbAsync(db => db.Memories.Where(m => m.StorytellerId == rose.Id).ToDictionaryAsync(m => m.Id, m => m.Status));
        Assert.Equal(MemoryStatus.Skipped, statuses[memoryId]);
        Assert.Equal(MemoryStatus.Declined, statuses[secondId]);

        var questions = Decode(await browser.Http.GetStringAsync("/questions"));
        Assert.Contains("Saved for later", questions);
        Assert.Contains("Chose not to answer", questions);
    }

    [Fact]
    public async Task Questions_not_yet_unlocked_cannot_be_opened()
    {
        await app.CreateUserAsync("Rose", "locked@example.test", [Roles.Storyteller], PromptSetKind.Core, Pacing.Weekly);
        var browser = await Browser.SignedInAsync(app, "locked@example.test");
        var later = await app.WithDbAsync(db => db.Prompts.Where(p => p.Key == "wisdom-letter").Select(p => p.Id).SingleAsync());

        var (path, html, _) = await browser.GetAsync($"/answer/{later}");
        Assert.Equal("/", path);
        Assert.Contains("That question opens on", Decode(html));
        Assert.Equal(0, await app.WithDbAsync(db => db.Memories.CountAsync(m => m.PromptId == later)));
    }

    [Fact]
    public async Task Autosave_saves_words_and_captions_and_reports_the_revision()
    {
        var rose = await app.CreateUserAsync("Rose", "autosave@example.test", [Roles.Storyteller], PromptSetKind.Core);
        var browser = await Browser.SignedInAsync(app, "autosave@example.test");
        var (memoryId, csrf, _) = await StoryClient.OpenFirstQuestionAsync(browser, app, rose.Id);
        var upload = await StoryClient.UploadAsync(browser.Http, csrf, memoryId, await File.ReadAllBytesAsync(AppFactory.Fixture("photo.jpg")), "p.jpg");
        var attachmentId = (await upload.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        var revision = await app.WithDbAsync(db => db.Memories.Where(m => m.Id == memoryId).Select(m => m.Revision).SingleAsync());

        var response = await browser.Http.SendAsync(StoryClient.Api(HttpMethod.Post, $"/api/memories/{memoryId}/autosave", csrf,
            JsonContent.Create(new
            {
                body = "Typed on the iPad",
                when = new { decade = 1950 },
                revision,
                captions = new Dictionary<Guid, string> { [attachmentId] = "Mom and Dad, 1952" },
            })));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(revision + 1, json.GetProperty("revision").GetInt32());

        var memory = await app.WithDbAsync(db => db.Memories.Include(m => m.Attachments).SingleAsync(m => m.Id == memoryId));
        Assert.Equal("Typed on the iPad", memory.Body);
        Assert.Equal("1950s", memory.When.Format());
        Assert.Equal("Mom and Dad, 1952", memory.Attachments.Single().Caption);

        // The same (now stale) revision again is a conflict, not a silent overwrite.
        var stale = await browser.Http.SendAsync(StoryClient.Api(HttpMethod.Post, $"/api/memories/{memoryId}/autosave", csrf,
            JsonContent.Create(new { body = "old tab", revision })));
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
    }

    [Fact]
    public async Task An_expired_session_gets_401_from_autosave_so_the_page_keeps_the_words()
    {
        var rose = await app.CreateUserAsync("Rose", "expired@example.test", [Roles.Storyteller], PromptSetKind.Core);
        var browser = await Browser.SignedInAsync(app, "expired@example.test");
        var (memoryId, csrf, page) = await StoryClient.OpenFirstQuestionAsync(browser, app, rose.Id);
        await browser.Http.PostFormAsync("/account/logout", page);

        var response = await browser.Http.SendAsync(StoryClient.Api(HttpMethod.Post, $"/api/memories/{memoryId}/autosave", csrf,
            JsonContent.Create(new { body = "words" })));
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Nobody_else_can_open_or_save_someone_elses_story()
    {
        var rose = await app.CreateUserAsync("Rose", "mine@example.test", [Roles.Storyteller], PromptSetKind.Core);
        var roseBrowser = await Browser.SignedInAsync(app, "mine@example.test");
        var (memoryId, _, _) = await StoryClient.OpenFirstQuestionAsync(roseBrowser, app, rose.Id);

        await app.CreateUserAsync("Cousin", "notmine@example.test", [Roles.Storyteller, Roles.Family], PromptSetKind.Core);
        var cousin = await Browser.SignedInAsync(app, "notmine@example.test");
        var cousinCsrf = Browser.CsrfFromPage((await StoryClient.OpenFirstQuestionAsync(cousin, app, Guid.Empty)).Html);

        Assert.Equal(HttpStatusCode.NotFound, (await cousin.Http.GetAsync($"/stories/{memoryId}/write")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await cousin.Http.GetAsync($"/stories/{memoryId}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await cousin.Http.GetAsync($"/questions?for={rose.Id}")).StatusCode);
        var autosave = await cousin.Http.SendAsync(StoryClient.Api(HttpMethod.Post, $"/api/memories/{memoryId}/autosave", cousinCsrf,
            JsonContent.Create(new { body = "not mine" })));
        Assert.Equal(HttpStatusCode.NotFound, autosave.StatusCode);

        // Admins can always see everything.
        var admin = await Browser.SignedInAsync(app, AppFactory.AdminEmail);
        Assert.Equal(HttpStatusCode.OK, (await admin.Http.GetAsync($"/stories/{memoryId}")).StatusCode);
    }

    [Fact]
    public async Task A_helper_writes_for_a_storyteller_and_the_story_says_so()
    {
        var rose = await app.CreateUserAsync("Grandma Rose", "grandma@example.test", [Roles.Storyteller], PromptSetKind.Core);
        var lily = await app.CreateUserAsync("Lily", "lily@example.test", [Roles.Family]);
        await app.WithServiceAsync<FamilyMemories.Admin.UserAdminService, FamilyMemories.Admin.AdminResult>(s => s.AddHelperAsync(rose.Id, lily.Id));

        var browser = await Browser.SignedInAsync(app, "lily@example.test");
        var home = Decode(await browser.Http.GetStringAsync("/"));
        Assert.Contains("Write for Grandma Rose", home);

        var prompt = await app.WithDbAsync(db => db.Prompts.Where(p => p.Key == "roots-born").Select(p => p.Id).SingleAsync());
        var (path, page, _) = await browser.GetAsync($"/answer/{prompt}?for={rose.Id}");
        Assert.Contains("You're writing for <strong>Grandma Rose</strong>", Decode(page));
        var memoryId = Guid.Parse(path.Split('/')[2]);

        await browser.Http.PostAsync($"/stories/{memoryId}/write?handler=Finish", Form(page, ("Body", "She was born in 1931.")));
        var memory = await app.WithDbAsync(db => db.Memories.SingleAsync(m => m.Id == memoryId));
        Assert.Equal(rose.Id, memory.StorytellerId);
        Assert.Equal(lily.Id, memory.WrittenById);
        Assert.Contains("Told by Grandma Rose · written by Lily", Decode(await browser.Http.GetStringAsync($"/stories/{memoryId}")));
    }
}

public sealed class AdminPageTests(PostgresFixture postgres) : IAsyncLifetime
{
    private readonly AppFactory app = new(postgres);
    public ValueTask InitializeAsync() => app.InitializeAsync();
    public ValueTask DisposeAsync() => app.DisposeAsync();

    private static string Decode(string html) => WebUtility.HtmlDecode(html);

    [Fact]
    public async Task Admin_pages_are_for_admins_only()
    {
        await app.CreateUserAsync("Rose", "notadmin@example.test", [Roles.Storyteller, Roles.Family], PromptSetKind.Core);
        var rose = await Browser.SignedInAsync(app, "notadmin@example.test");
        foreach (var url in new[] { "/admin", "/admin/users", "/admin/prompts", "/admin/prompt-sets", "/admin/audit" })
        {
            var response = await rose.Http.GetAsync(url);
            Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
            Assert.Contains("/account/access-denied", response.Headers.Location!.ToString());
        }
    }

    [Fact]
    public async Task Inviting_someone_creates_them_and_emails_a_link_that_signs_them_in()
    {
        var admin = await Browser.SignedInAsync(app, AppFactory.AdminEmail);
        var page = await admin.Http.GetStringAsync("/admin/users/new");
        var coreSet = await app.WithDbAsync(db => db.PromptSets.Where(s => s.Kind == PromptSetKind.Core).Select(s => s.Id).SingleAsync());

        var post = await admin.Http.PostFormAsync("/admin/users/new", page,
            ("Form.DisplayName", "Grandpa Joe"), ("Form.Email", "joe@example.test"), ("Form.Roles", "Storyteller"),
            ("Form.PromptSetId", coreSet.ToString()), ("Form.Pacing", "Weekly"), ("SendEmail", "true"));
        Assert.Equal(HttpStatusCode.Redirect, post.StatusCode);

        var joe = await app.WithDbAsync(db => db.Users.SingleAsync(u => u.Email == "joe@example.test"));
        Assert.Equal(coreSet, joe.PromptSetId);
        Assert.NotNull(joe.InvitedAt);
        var roles = await app.WithDbAsync(db => (from ur in db.UserRoles join r in db.Roles on ur.RoleId equals r.Id where ur.UserId == joe.Id select r.Name).ToListAsync());
        Assert.Equivalent(new[] { Roles.Storyteller, Roles.Family }, roles);

        var email = app.Email.To("joe@example.test").Single();
        Assert.Contains("Jacob Admin invited you", email.Subject);
        var joeBrowser = new Browser(app);
        await joeBrowser.RedeemAsync(CapturingEmailSender.ExtractMagicLink(email));
        Assert.Contains("Hello, Grandpa", Decode(await joeBrowser.Http.GetStringAsync("/")));
        Assert.True(await app.WithDbAsync(db => db.AuditLog.AnyAsync(a => a.Action == "user.invite")));
    }

    [Fact]
    public async Task A_duplicate_email_is_refused_in_plain_language()
    {
        var admin = await Browser.SignedInAsync(app, AppFactory.AdminEmail);
        var page = await admin.Http.GetStringAsync("/admin/users/new");
        var post = await admin.Http.PostFormAsync("/admin/users/new", page,
            ("Form.DisplayName", "Copy"), ("Form.Email", AppFactory.AdminEmail), ("Form.Pacing", "Weekly"));
        Assert.Equal(HttpStatusCode.OK, post.StatusCode);
        Assert.Contains("already on the list", Decode(await post.Content.ReadAsStringAsync()));
    }

    [Fact]
    public async Task A_printed_login_card_holds_a_single_use_30_day_link_and_is_audited()
    {
        var rose = await app.CreateUserAsync("Rose", "card@example.test", [Roles.Storyteller], PromptSetKind.Core);
        var admin = await Browser.SignedInAsync(app, AppFactory.AdminEmail);
        var page = await admin.Http.GetStringAsync($"/admin/users/{rose.Id}");

        // GET never mints a link (so previews and reloads can't).
        Assert.Equal(HttpStatusCode.Redirect, (await admin.Http.GetAsync($"/admin/users/{rose.Id}/login-card")).StatusCode);

        var card = await admin.Http.PostFormAsync($"/admin/users/{rose.Id}/login-card", page);
        var html = Decode(await card.Content.ReadAsStringAsync());
        Assert.Contains("Login card for Rose", html);
        Assert.Contains("src=\"data:image/png;base64,", html);
        Assert.Contains("Point it at the square above", html);

        var token = await app.WithDbAsync(db => db.MagicLinkTokens.SingleAsync(t => t.UserId == rose.Id && t.Purpose == MagicLinkPurpose.DeviceEnrollment));
        Assert.InRange(token.ExpiresAt - token.CreatedAt, TimeSpan.FromDays(29.9), TimeSpan.FromDays(30.1));
        Assert.True(await app.WithDbAsync(db => db.AuditLog.AnyAsync(a => a.Action == "logincard.print" && a.TargetId == rose.Id.ToString())));
    }

    [Fact]
    public async Task Admin_can_sign_out_one_device()
    {
        var rose = await app.CreateUserAsync("Rose", "device@example.test", [Roles.Storyteller], PromptSetKind.Core);
        var roseBrowser = await Browser.SignedInAsync(app, "device@example.test");
        Assert.Equal(HttpStatusCode.OK, (await roseBrowser.Http.GetAsync("/")).StatusCode);

        var admin = await Browser.SignedInAsync(app, AppFactory.AdminEmail);
        var page = await admin.Http.GetStringAsync($"/admin/users/{rose.Id}");
        Assert.Contains("Sign out this device", page);
        var deviceId = await app.WithDbAsync(db => db.UserDevices.Where(d => d.UserId == rose.Id).Select(d => d.Id).SingleAsync());
        await admin.Http.PostFormAsync($"/admin/users/{rose.Id}?handler=RevokeDevice", page, ("deviceId", deviceId.ToString()));

        var after = await roseBrowser.Http.GetAsync("/");
        Assert.Equal(HttpStatusCode.Redirect, after.StatusCode);
        Assert.Contains("/account/login", after.Headers.Location!.ToString());
    }

    [Fact]
    public async Task Pausing_stops_sign_in_and_admins_cannot_pause_themselves()
    {
        var rose = await app.CreateUserAsync("Rose", "pause@example.test", [Roles.Storyteller], PromptSetKind.Core);
        var roseBrowser = await Browser.SignedInAsync(app, "pause@example.test");
        var admin = await Browser.SignedInAsync(app, AppFactory.AdminEmail);

        var page = await admin.Http.GetStringAsync($"/admin/users/{rose.Id}");
        await admin.Http.PostFormAsync($"/admin/users/{rose.Id}?handler=Pause", page, ("paused", "true"));
        Assert.Equal(HttpStatusCode.Redirect, (await roseBrowser.Http.GetAsync("/")).StatusCode);

        var adminId = await app.WithDbAsync(db => db.Users.Where(u => u.Email == AppFactory.AdminEmail).Select(u => u.Id).SingleAsync());
        var self = await admin.Http.GetStringAsync($"/admin/users/{adminId}");
        await admin.Http.PostFormAsync($"/admin/users/{adminId}?handler=Pause", self, ("paused", "true"));
        Assert.Contains("You can't pause your own account", Decode(await admin.Http.GetStringAsync($"/admin/users/{adminId}")));
    }

    [Fact]
    public async Task Editing_a_question_sticks_and_turning_one_off_hides_it()
    {
        var admin = await Browser.SignedInAsync(app, AppFactory.AdminEmail);
        var prompt = await app.WithDbAsync(db => db.Prompts.SingleAsync(p => p.Key == "roots-earliest"));
        var page = await admin.Http.GetStringAsync($"/admin/prompts/{prompt.Id}");
        await admin.Http.PostFormAsync($"/admin/prompts/{prompt.Id}", page,
            ("ChapterId", prompt.ChapterId.ToString()!), ("Question", "What is the very first thing you remember?"),
            ("Title", "Earliest memory"), ("Hints", "How old were you?\nWho was there?"), ("IsCore", "true"));

        var edited = await app.WithDbAsync(db => db.Prompts.SingleAsync(p => p.Id == prompt.Id));
        Assert.Equal("What is the very first thing you remember?", edited.Question);
        Assert.Equal(["How old were you?", "Who was there?"], edited.Hints);
        Assert.True(edited.EditedByAdmin);

        var list = await admin.Http.GetStringAsync("/admin/prompts");
        await admin.Http.PostFormAsync("/admin/prompts?handler=Toggle", list, ("promptId", prompt.Id.ToString()));
        var rose = await app.CreateUserAsync("Rose", "toggle@example.test", [Roles.Storyteller], PromptSetKind.Core);
        var plan = await app.WithServiceAsync<FamilyMemories.Stories.StoryPlanService, FamilyMemories.Stories.StoryPlan>(s => s.GetAsync(rose.Id));
        Assert.Equal(25, plan.Slots.Count);
        Assert.True(await app.WithDbAsync(db => db.AuditLog.AnyAsync(a => a.Action == "prompt.off")));
    }

    [Fact]
    public async Task Version_history_can_be_restored_by_an_admin()
    {
        var rose = await app.CreateUserAsync("Rose", "history@example.test", [Roles.Storyteller], PromptSetKind.Core);
        var roseBrowser = await Browser.SignedInAsync(app, "history@example.test");
        var (memoryId, csrf, page) = await StoryClient.OpenFirstQuestionAsync(roseBrowser, app, rose.Id);
        foreach (var body in new[] { "First draft", "Accidentally erased" })
        {
            var form = new MultipartFormDataContent
            {
                { new StringContent(HtmlForms.AntiforgeryToken(page)), "__RequestVerificationToken" },
                { new StringContent(body), "Body" },
            };
            await roseBrowser.Http.PostAsync($"/stories/{memoryId}/write?handler=Save", form);
        }

        var admin = await Browser.SignedInAsync(app, AppFactory.AdminEmail);
        var history = Decode(await admin.Http.GetStringAsync($"/admin/stories/{memoryId}/history"));
        Assert.Contains("Version 1", history);
        Assert.Contains("Version 2", history);
        await admin.Http.PostFormAsync($"/admin/stories/{memoryId}/history", history, ("number", "1"));
        Assert.Equal("First draft", await app.WithDbAsync(db => db.Memories.Where(m => m.Id == memoryId).Select(m => m.Body).SingleAsync()));

        Assert.Equal(HttpStatusCode.Redirect, (await roseBrowser.Http.GetAsync($"/admin/stories/{memoryId}/history")).StatusCode);
    }
}
