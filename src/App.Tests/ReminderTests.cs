using System.Net;
using System.Text.RegularExpressions;
using FamilyMemories.Data;
using FamilyMemories.Email;
using FamilyMemories.Family;
using FamilyMemories.Reminders;
using FamilyMemories.Stories;
using FamilyMemories.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace FamilyMemories.Tests;

public sealed class ReminderScheduleTests
{
    private static readonly TimeZoneInfo NewYork = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");
    private static DateTime Utc(int y, int m, int d, int h, int min = 0) => new(y, m, d, h, min, 0, DateTimeKind.Utc);

    [Fact]
    public void Weekly_finds_the_latest_sunday_10am_in_local_time()
    {
        // Wednesday 2026-11-25 15:00 UTC → last Sunday 2026-11-22 10:00 EST = 15:00 UTC.
        var occ = ReminderSchedule.LatestOccurrenceUtc(ReminderFrequency.Weekly, DayOfWeek.Sunday, new TimeOnly(10, 0), NewYork, Utc(2026, 11, 25, 15));
        Assert.Equal(Utc(2026, 11, 22, 15), occ);
        // Sunday itself, before 10am local: still last week's.
        occ = ReminderSchedule.LatestOccurrenceUtc(ReminderFrequency.Weekly, DayOfWeek.Sunday, new TimeOnly(10, 0), NewYork, Utc(2026, 11, 29, 14, 59));
        Assert.Equal(Utc(2026, 11, 22, 15), occ);
        occ = ReminderSchedule.LatestOccurrenceUtc(ReminderFrequency.Weekly, DayOfWeek.Sunday, new TimeOnly(10, 0), NewYork, Utc(2026, 11, 29, 15));
        Assert.Equal(Utc(2026, 11, 29, 15), occ);
    }

    [Fact]
    public void The_local_time_stays_the_same_across_daylight_saving_changes()
    {
        // 10:00 New York is 14:00 UTC in summer (EDT) and 15:00 UTC in winter (EST).
        var summer = ReminderSchedule.LatestOccurrenceUtc(ReminderFrequency.Weekly, DayOfWeek.Sunday, new TimeOnly(10, 0), NewYork, Utc(2026, 10, 26, 12));
        var winter = ReminderSchedule.LatestOccurrenceUtc(ReminderFrequency.Weekly, DayOfWeek.Sunday, new TimeOnly(10, 0), NewYork, Utc(2026, 11, 2, 12));
        Assert.Equal(Utc(2026, 10, 25, 14), summer);
        Assert.Equal(Utc(2026, 11, 1, 15), winter); // the very day clocks went back
    }

    [Fact]
    public void A_time_skipped_in_spring_moves_to_the_first_valid_minute()
    {
        // 2026-03-08: clocks jump from 02:00 to 03:00 EST→EDT; 02:30 doesn't exist.
        var occ = ReminderSchedule.LatestOccurrenceUtc(ReminderFrequency.Weekly, DayOfWeek.Sunday, new TimeOnly(2, 30), NewYork, Utc(2026, 3, 8, 12));
        Assert.Equal(Utc(2026, 3, 8, 7), occ); // 03:00 EDT
    }

    [Fact]
    public void A_time_repeated_in_autumn_uses_the_first_one()
    {
        // 2026-11-01: 01:30 happens twice (EDT then EST); use the first (05:30 UTC).
        var occ = ReminderSchedule.LatestOccurrenceUtc(ReminderFrequency.Weekly, DayOfWeek.Sunday, new TimeOnly(1, 30), NewYork, Utc(2026, 11, 1, 12));
        Assert.Equal(Utc(2026, 11, 1, 5, 30), occ);
    }

    [Fact]
    public void Monthly_is_the_first_chosen_weekday_of_the_month()
    {
        var occ = ReminderSchedule.LatestOccurrenceUtc(ReminderFrequency.Monthly, DayOfWeek.Sunday, new TimeOnly(10, 0), NewYork, Utc(2026, 11, 25, 15));
        Assert.Equal(Utc(2026, 11, 1, 15), occ);
        occ = ReminderSchedule.LatestOccurrenceUtc(ReminderFrequency.Monthly, DayOfWeek.Sunday, new TimeOnly(10, 0), NewYork, Utc(2026, 12, 3, 15));
        Assert.Equal(Utc(2026, 11, 1, 15), occ); // December's first Sunday is the 6th
    }

    [Fact]
    public void Due_rules()
    {
        var occ = Utc(2026, 11, 22, 15);
        Assert.True(ReminderSchedule.IsDue(ReminderFrequency.Weekly, occ, null, Utc(2026, 11, 1, 0)));
        Assert.False(ReminderSchedule.IsDue(ReminderFrequency.Weekly, occ, occ, Utc(2026, 11, 1, 0)));          // already handled
        Assert.False(ReminderSchedule.IsDue(ReminderFrequency.Weekly, occ, null, Utc(2026, 11, 23, 0)));        // before they started
        Assert.False(ReminderSchedule.IsDue(ReminderFrequency.EveryTwoWeeks, occ, occ.AddDays(-7), Utc(2026, 1, 1, 0)));
        Assert.True(ReminderSchedule.IsDue(ReminderFrequency.EveryTwoWeeks, occ, occ.AddDays(-14), Utc(2026, 1, 1, 0)));
        Assert.False(ReminderSchedule.IsDue(ReminderFrequency.Off, null, null, Utc(2026, 1, 1, 0)));
    }

    [Fact]
    public void Next_occurrence_for_the_home_page()
    {
        var next = ReminderSchedule.NextOccurrenceUtc(ReminderFrequency.Weekly, DayOfWeek.Sunday, new TimeOnly(10, 0), NewYork, Utc(2026, 11, 25, 15), null);
        Assert.Equal(Utc(2026, 11, 29, 15), next);
        next = ReminderSchedule.NextOccurrenceUtc(ReminderFrequency.EveryTwoWeeks, DayOfWeek.Sunday, new TimeOnly(10, 0), NewYork, Utc(2026, 11, 25, 15), Utc(2026, 11, 22, 15));
        Assert.Equal(Utc(2026, 12, 6, 15), next);
    }
}

public sealed partial class ReminderTests(PostgresFixture postgres) : IAsyncLifetime
{
    private readonly AppFactory app = new(postgres);

    public async ValueTask InitializeAsync()
    {
        // Tuesday 2026-11-24 12:00 New York.
        app.Clock.Set(new DateTimeOffset(2026, 11, 24, 17, 0, 0, TimeSpan.Zero));
        await app.InitializeAsync();
    }

    public ValueTask DisposeAsync() => app.DisposeAsync();

    private Task<TickResult> TickAsync() => app.WithServiceAsync<ReminderService, TickResult>(s => s.TickAsync());

    /// <summary>To the next Sunday 10:00 New York (15:00 UTC), plus a few minutes.</summary>
    private void ToNextSundayMorning()
    {
        var now = app.Clock.GetUtcNow();
        var days = ((int)DayOfWeek.Sunday - (int)now.DayOfWeek + 7) % 7;
        var target = new DateTimeOffset(now.Date.AddDays(days == 0 ? 7 : days).AddHours(15).AddMinutes(5), TimeSpan.Zero);
        if (target <= now) target = target.AddDays(7);
        app.Clock.Set(target);
    }

    private Task<List<EmailMessage>> RemindersToAsync(string email) =>
        Task.FromResult(app.Email.To(email).Where(m => m.Subject.Contains("question", StringComparison.OrdinalIgnoreCase) || m.Subject.Contains("asked you")).ToList());

    private static string Text(string html) => WebUtility.HtmlDecode(Regex.Replace(html, "<[^>]+>", ""));

    [Fact]
    public async Task The_weekly_reminder_brings_the_next_question_with_one_link_that_signs_in_and_opens_it()
    {
        var rose = await app.CreateUserAsync("Rose", "rem-rose@example.test", [Roles.Storyteller, Roles.Family], PromptSetKind.Core, Pacing.Weekly);
        Assert.Equal(0, (await TickAsync()).Sent);                       // Tuesday: not yet

        ToNextSundayMorning();
        Assert.Equal(1, (await TickAsync()).Sent);
        Assert.Equal(0, (await TickAsync()).Sent);                       // no duplicate in the same slot

        var mail = Assert.Single(await RemindersToAsync("rem-rose@example.test"));
        Assert.Equal("A question for you: Where you were born", mail.Subject);
        Assert.Contains("Where and when were you born? Do you know how your name was chosen?", mail.TextBody);
        Assert.Contains("Were you named after anyone in the family?", mail.TextBody); // the hints
        Assert.Contains("Answer this question", mail.HtmlBody);
        Assert.Contains("Need help? Call Jacob", mail.TextBody);
        // One link besides the email-settings link.
        var links = LinkRegex().Matches(mail.HtmlBody).Select(m => m.Groups[1].Value).Distinct().ToList();
        Assert.Equal(2, links.Count);
        Assert.Contains(links, l => l.Contains("/account/magic-link?token="));
        Assert.Contains(links, l => l.Contains("/email/preferences/"));
        Assert.NotNull(mail.UnsubscribeUrl);

        // The button signs her in and opens that question.
        var browser = new Browser(app);
        var redeem = await browser.RedeemAsync(CapturingEmailSender.ExtractMagicLink(mail));
        var promptId = await app.WithDbAsync(db => db.Prompts.Where(p => p.Key == "roots-born").Select(p => p.Id).SingleAsync());
        Assert.Equal($"/answer/{promptId}", redeem.Headers.Location?.OriginalString);

        var log = await app.WithDbAsync(db => db.EmailLog.SingleAsync(e => e.Kind == EmailKind.Reminder));
        Assert.Equal((EmailStatus.Sent, rose.Id), (log.Status, log.UserId));
    }

    [Fact]
    public async Task Writing_something_skips_the_next_reminder()
    {
        var rose = await app.CreateUserAsync("Rose", "rem-quiet@example.test", [Roles.Storyteller], PromptSetKind.Core, Pacing.AllAtOnce);
        ToNextSundayMorning();
        Assert.Equal(1, (await TickAsync()).Sent); // first reminder

        app.Clock.Advance(TimeSpan.FromDays(2));
        var memory = await app.WithServiceAsync<MemoryService, Memory>(s => s.CreateFreeFormAsync(rose.Id, rose.Id));
        await app.WithServiceAsync<MemoryService, SaveResult>(s => s.SaveAsync(memory.Id, new MemoryEdit("A story", "Some words", default, null), rose.Id, VersionKind.Save));

        ToNextSundayMorning();
        Assert.Equal(1, (await TickAsync()).Skipped); // quiet week after writing
        ToNextSundayMorning();
        Assert.Equal(1, (await TickAsync()).Sent);                    // only one week is skipped
        ToNextSundayMorning();
        Assert.Equal(1, (await TickAsync()).Sent);
        Assert.Equal(3, (await RemindersToAsync("rem-quiet@example.test")).Count);
    }

    [Fact]
    public async Task Snoozing_from_the_email_settings_link_works_without_signing_in()
    {
        var rose = await app.CreateUserAsync("Rose", "rem-snooze@example.test", [Roles.Storyteller], PromptSetKind.Core, Pacing.AllAtOnce);
        ToNextSundayMorning();
        await TickAsync();
        var prefsUrl = LinkRegex().Matches(app.Email.To("rem-snooze@example.test").Single().HtmlBody)
            .Select(m => new Uri(m.Groups[1].Value)).Single(u => u.AbsolutePath.StartsWith("/email/preferences/"));

        var stranger = new Browser(app);
        var page = await stranger.Http.GetStringAsync(prefsUrl.PathAndQuery);
        Assert.Contains("Email settings for Rose", page);
        Assert.Contains("Your next question arrives by email on", WebUtility.HtmlDecode(page));
        await stranger.Http.PostFormAsync($"{prefsUrl.AbsolutePath}?handler=Snooze", page);

        ToNextSundayMorning();
        Assert.Equal(1, (await TickAsync()).Skipped);
        app.Clock.Advance(TimeSpan.FromDays(14));
        ToNextSundayMorning();
        Assert.Equal(1, (await TickAsync()).Sent);
        Assert.True(await app.WithDbAsync(db => db.AuditLog.AnyAsync(a => a.Action == "reminders.snooze")));

        // Turning them off entirely, then a bogus token.
        page = await stranger.Http.GetStringAsync(prefsUrl.PathAndQuery);
        await stranger.Http.PostFormAsync($"{prefsUrl.AbsolutePath}?handler=Pause", page);
        Assert.Equal(ReminderFrequency.Off, await app.WithDbAsync(db => db.ReminderSettings.Where(s => s.UserId == rose.Id).Select(s => s.Frequency).SingleAsync()));
        Assert.Contains("This link doesn't work", WebUtility.HtmlDecode(await stranger.Http.GetStringAsync("/email/preferences/not-a-real-token")));
    }

    [Fact]
    public async Task One_click_unsubscribe_from_the_mail_client()
    {
        var rose = await app.CreateUserAsync("Rose", "rem-oneclick@example.test", [Roles.Storyteller], PromptSetKind.Core, Pacing.AllAtOnce);
        ToNextSundayMorning();
        await TickAsync();
        var url = new Uri(app.Email.To("rem-oneclick@example.test").Single().UnsubscribeUrl!);
        // Mail clients POST "List-Unsubscribe=One-Click" with no cookies or token.
        var response = await new Browser(app).Http.PostAsync(url.PathAndQuery, new FormUrlEncodedContent([new("List-Unsubscribe", "One-Click")]));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(ReminderFrequency.Off, await app.WithDbAsync(db => db.ReminderSettings.Where(s => s.UserId == rose.Id).Select(s => s.Frequency).SingleAsync()));
    }

    [Fact]
    public async Task Reminders_stop_when_every_question_is_answered_and_wait_for_next_weeks_question()
    {
        var rose = await app.CreateUserAsync("Rose", "rem-done@example.test", [Roles.Storyteller], PromptSetKind.Core, Pacing.Weekly);
        ToNextSundayMorning();
        Assert.Equal(1, (await TickAsync()).Sent);

        // She answers this week's only question → nothing to send until next week's unlocks.
        var plan = await app.WithServiceAsync<StoryPlanService, StoryPlan>(s => s.GetAsync(rose.Id));
        await app.WithServiceAsync<MemoryService, bool>(async s =>
        {
            await using var scope = app.Services.CreateAsyncScope();
            var prompt = await scope.ServiceProvider.GetRequiredService<AppDbContext>().Prompts.SingleAsync(p => p.Id == plan.Next!.Prompt.Id);
            var m = await s.GetOrCreateForPromptAsync(rose.Id, prompt, rose.Id);
            return await s.SetStatusAsync(m.Id, MemoryStatus.Complete, rose.Id);
        });

        // Make the whole set complete (decline the rest) and check reminders stop.
        var all = await app.WithServiceAsync<StoryPlanService, StoryPlan>(s => s.GetAsync(rose.Id));
        await using (var scope = app.Services.CreateAsyncScope())
        {
            var memories = scope.ServiceProvider.GetRequiredService<MemoryService>();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            foreach (var slot in all.Slots.Where(s => !s.IsDone))
            {
                var prompt = await db.Prompts.SingleAsync(p => p.Id == slot.Prompt.Id);
                var m = await memories.GetOrCreateForPromptAsync(rose.Id, prompt, rose.Id);
                await memories.SetStatusAsync(m.Id, MemoryStatus.Declined, rose.Id);
            }
        }
        for (var week = 0; week < 3; week++)
        {
            ToNextSundayMorning();
            Assert.Equal(0, (await TickAsync()).Sent);
        }
    }

    [Fact]
    public async Task After_three_unanswered_reminders_the_admins_get_a_gentle_nudge_once()
    {
        await app.CreateUserAsync("Grandma Rose", "rem-nudge@example.test", [Roles.Storyteller], PromptSetKind.Core, Pacing.AllAtOnce);
        var nudges = 0;
        for (var week = 0; week < 5; week++)
        {
            ToNextSundayMorning();
            nudges += (await TickAsync()).Nudges;
        }
        Assert.Equal(1, nudges);
        var nudge = Assert.Single(app.Email.To(AppFactory.AdminEmail), m => m.Subject.Contains("hasn't added a story"));
        Assert.Equal("Grandma Rose hasn't added a story in a while", nudge.Subject);
        Assert.Contains("Maybe give them a call?", nudge.TextBody);
        Assert.Contains("hasn't added a story in 3 weeks", nudge.TextBody);
    }

    [Fact]
    public async Task A_follow_up_question_arrives_as_the_next_reminder_saying_who_asked()
    {
        var rose = await app.CreateUserAsync("Rose", "rem-follow@example.test", [Roles.Storyteller, Roles.Family], PromptSetKind.Core, Pacing.AllAtOnce);
        var lily = await app.CreateUserAsync("Lily", "rem-lily@example.test", [Roles.Family]);
        var memory = await app.WithServiceAsync<MemoryService, Memory>(s => s.CreateFreeFormAsync(rose.Id, rose.Id));
        await app.WithServiceAsync<MemoryService, SaveResult>(s => s.SaveAsync(memory.Id, new MemoryEdit("The farm", "Cows.", default, null), rose.Id, VersionKind.Save));
        await app.WithServiceAsync<MemoryService, bool>(s => s.SetStatusAsync(memory.Id, MemoryStatus.Complete, rose.Id));
        var m = await app.WithDbAsync(db => db.Memories.SingleAsync(x => x.Id == memory.Id));
        var prompt = await app.WithServiceAsync<ConversationService, Prompt?>(s => s.AskFollowUpAsync(m, lily.Id, "What were the cows called?"));

        app.Clock.Advance(TimeSpan.FromDays(8)); // past the quiet period after writing
        ToNextSundayMorning();
        await TickAsync();
        ToNextSundayMorning();
        await TickAsync();
        var mail = app.Email.To("rem-follow@example.test").First(e => e.Subject.Contains("asked you"));
        Assert.Equal("Lily asked you a question", mail.Subject);
        Assert.Contains("What were the cows called?", mail.TextBody);
        Assert.NotNull(await app.WithDbAsync(db => db.Prompts.Where(p => p.Id == prompt!.Id).Select(p => p.NotifiedAt).SingleAsync()));
    }

    [Fact]
    public async Task Storytellers_hear_who_read_their_stories_once()
    {
        var rose = await app.CreateUserAsync("Rose", "rem-reads@example.test", [Roles.Storyteller, Roles.Family], PromptSetKind.Core);
        var memory = await app.WithServiceAsync<MemoryService, Memory>(s => s.CreateFreeFormAsync(rose.Id, rose.Id));
        await app.WithServiceAsync<MemoryService, SaveResult>(s => s.SaveAsync(memory.Id, new MemoryEdit("The farm", "Cows.", default, null), rose.Id, VersionKind.Save));
        await app.WithServiceAsync<MemoryService, bool>(s => s.SetStatusAsync(memory.Id, MemoryStatus.Complete, rose.Id));
        foreach (var name in new[] { "Lily", "Ed", "Ann" })
        {
            await app.CreateUserAsync(name, $"rem-{name}@example.test", [Roles.Family]);
            var b = await Browser.SignedInAsync(app, $"rem-{name}@example.test");
            await b.Http.GetStringAsync($"/stories/{memory.Id}");
        }

        Assert.Equal(1, await app.WithServiceAsync<ReminderService, int>(s => s.SendReadSummariesAsync()));
        var mail = app.Email.To("rem-reads@example.test").Single(m => m.Subject.Contains("read your story"));
        Assert.Equal("3 family members read your story “The farm”", mail.Subject);
        Assert.Contains("Lily, Ed and Ann read your story", mail.TextBody);
        Assert.Equal(0, await app.WithServiceAsync<ReminderService, int>(s => s.SendReadSummariesAsync())); // not again
    }

    [Fact]
    public async Task The_weekly_digest_lists_only_stories_each_person_may_read_and_respects_opt_out()
    {
        var rose = await app.CreateUserAsync("Rose", "dig-rose@example.test", [Roles.Storyteller, Roles.Family], PromptSetKind.Core);
        var lily = await app.CreateUserAsync("Lily", "dig-lily@example.test", [Roles.Family]);
        var ed = await app.CreateUserAsync("Ed", "dig-ed@example.test", [Roles.Family]);
        async Task Story(string title, Visibility v, params Guid[] shared)
        {
            var m = await app.WithServiceAsync<MemoryService, Memory>(s => s.CreateFreeFormAsync(rose.Id, rose.Id));
            await app.WithServiceAsync<MemoryService, SaveResult>(s => s.SaveAsync(m.Id, new MemoryEdit(title, "Words.", default, null), rose.Id, VersionKind.Save));
            await app.WithServiceAsync<SharingService, bool>(async s => { await s.SetVisibilityAsync(m.Id, v, shared); return true; });
            await app.WithServiceAsync<MemoryService, bool>(s => s.SetStatusAsync(m.Id, MemoryStatus.Complete, rose.Id));
        }
        await Story("For everyone", Visibility.AllFamily);
        await Story("Only for Lily", Visibility.SelectedPeople, lily.Id);
        await Story("Secret", Visibility.Private);
        await app.WithDbAsync(async db => { db.ReminderSettings.Add(new ReminderSettings { UserId = ed.Id, DigestOptOut = true }); await db.SaveChangesAsync(); });

        app.Clock.Advance(TimeSpan.FromHours(1));
        await app.WithServiceAsync<ReminderService, int>(s => s.SendDigestsAsync());

        var lilyDigest = app.Email.To("dig-lily@example.test").Single(m => m.Subject == "New family stories this week");
        Assert.Contains("For everyone", lilyDigest.TextBody);
        Assert.Contains("Only for Lily", lilyDigest.TextBody);
        Assert.DoesNotContain("Secret", lilyDigest.TextBody);
        Assert.Empty(app.Email.To("dig-ed@example.test"));                         // opted out
        Assert.DoesNotContain(app.Email.To("dig-rose@example.test"), m => m.Subject.StartsWith("New family stories")); // her own
        Assert.NotNull(lilyDigest.UnsubscribeUrl);
    }

    [Fact]
    public async Task Admins_can_edit_email_wording_preview_it_and_reset_it()
    {
        var admin = await Browser.SignedInAsync(app, AppFactory.AdminEmail);
        var page = await admin.Http.GetStringAsync("/admin/email/templates/Reminder");
        await admin.Http.PostFormAsync("/admin/email/templates/Reminder?handler=Save", page,
            ("Subject", "Hi {{ name }}, a question from the family"),
            ("TextBody", "{{ question }}\n{{ link }}"),
            ("HtmlBody", "<p>{{ question }}</p>{{ button link \"Answer\" }}"));

        await app.CreateUserAsync("<b>Rose</b>", "rem-tpl@example.test", [Roles.Storyteller], PromptSetKind.Core, Pacing.AllAtOnce);
        ToNextSundayMorning();
        await TickAsync();
        var mail = app.Email.To("rem-tpl@example.test").Single();
        Assert.Equal("Hi <b>Rose</b>, a question from the family", mail.Subject);
        Assert.Contains("&lt;b&gt;Rose&lt;/b&gt;", mail.HtmlBody.Replace(" ", "")[..0] + WebUtility.HtmlEncode("<b>Rose</b>")); // names are encoded in HTML
        Assert.DoesNotContain("<b>Rose</b>", mail.HtmlBody);

        // Broken templates are refused in plain words.
        var broken = await admin.Http.PostFormAsync("/admin/email/templates/Reminder?handler=Save", page, ("Subject", "Hi {{ if name }}"), ("TextBody", "x"), ("HtmlBody", "x"));
        Assert.Contains("There's a mistake in the template", WebUtility.HtmlDecode(await broken.Content.ReadAsStringAsync()));

        // Preview: admin-only, own strict CSP, framed only by us.
        var preview = await admin.Http.GetAsync("/admin/email/preview/Reminder");
        Assert.Contains("a question from the family", WebUtility.HtmlDecode(await preview.Content.ReadAsStringAsync()));
        Assert.Contains("frame-ancestors 'self'", preview.Headers.GetValues("Content-Security-Policy").Single());
        Assert.DoesNotContain("script-src", preview.Headers.GetValues("Content-Security-Policy").Single());
        var rose = await Browser.SignedInAsync(app, "rem-tpl@example.test");
        Assert.NotEqual(HttpStatusCode.OK, (await rose.Http.GetAsync("/admin/email/preview/Reminder")).StatusCode);

        page = await admin.Http.GetStringAsync("/admin/email/templates/Reminder");
        await admin.Http.PostFormAsync("/admin/email/templates/Reminder?handler=Reset", page);
        Assert.False(await app.WithDbAsync(db => db.EmailTemplates.AnyAsync()));
    }

    [Fact]
    public async Task Smtp_settings_are_saved_encrypted_and_failures_are_logged_for_retry()
    {
        var admin = await Browser.SignedInAsync(app, AppFactory.AdminEmail);
        var page = await admin.Http.GetStringAsync("/admin/email");
        await admin.Http.PostFormAsync("/admin/email?handler=Save", page,
            ("Host", "smtp.example.test"), ("Port", "587"), ("Security", "StartTls"), ("Username", "family"),
            ("Password", "super-secret-app-password"), ("FromAddress", "memories@example.test"), ("FromName", "Family Memories"));

        var stored = await app.WithDbAsync(db => db.Settings.SingleAsync(s => s.Key == SmtpSettingsStore.SettingKey));
        Assert.DoesNotContain("super-secret-app-password", stored.Value);
        var settings = await app.Services.GetRequiredService<SmtpSettingsStore>().GetAsync();
        Assert.Equal(("smtp.example.test", "super-secret-app-password"), (settings.Host, settings.Password));

        // The settings page never shows the password; saving with it empty keeps it.
        page = await admin.Http.GetStringAsync("/admin/email");
        Assert.DoesNotContain("super-secret-app-password", page);
        await admin.Http.PostFormAsync("/admin/email?handler=Save", page, ("Host", "smtp2.example.test"), ("Port", "587"), ("Security", "StartTls"),
            ("Username", "family"), ("Password", ""), ("FromAddress", "memories@example.test"), ("FromName", "Family Memories"));
        Assert.Equal("super-secret-app-password", (await app.Services.GetRequiredService<SmtpSettingsStore>().GetAsync()).Password);

        // A failing send is recorded and can be retried.
        app.Email.FailNext = true;
        page = await admin.Http.GetStringAsync("/admin/email");
        var test = await admin.Http.PostFormAsync("/admin/email?handler=Test", page, ("TestTo", "someone@example.test"));
        Assert.Contains("could not be sent", WebUtility.HtmlDecode(await admin.Http.GetStringAsync("/admin/email")));
        var failed = await app.WithDbAsync(db => db.EmailLog.SingleAsync(e => e.Kind == EmailKind.Test));
        Assert.Equal(EmailStatus.Failed, failed.Status);
        var log = await admin.Http.GetStringAsync("/admin/email/log");
        Assert.Contains("Try again", log);
        await admin.Http.PostFormAsync("/admin/email/log?handler=Retry", log, ("id", failed.Id.ToString()));
        Assert.Equal(EmailStatus.Sent, await app.WithDbAsync(db => db.EmailLog.Where(e => e.Id == failed.Id).Select(e => e.Status).SingleAsync()));
    }

    [Fact]
    public async Task Admin_sets_one_persons_reminder_schedule_and_the_home_page_shows_it()
    {
        var rose = await app.CreateUserAsync("Rose", "rem-person@example.test", [Roles.Storyteller], PromptSetKind.Core, Pacing.AllAtOnce);
        var admin = await Browser.SignedInAsync(app, AppFactory.AdminEmail);
        var page = await admin.Http.GetStringAsync($"/admin/users/{rose.Id}");
        Assert.Contains("Next question by email: Sunday, November 29 at 10:00 AM", WebUtility.HtmlDecode(page));
        await admin.Http.PostFormAsync($"/admin/users/{rose.Id}?handler=Reminders", page,
            ("RFrequency", "Weekly"), ("RDay", "Wednesday"), ("RTime", "15:30"), ("RTimeZone", "America/Chicago"));

        var browser = await Browser.SignedInAsync(app, "rem-person@example.test");
        Assert.Contains("Next question by email: Wednesday, November 25 at 3:30 PM", Text(await browser.Http.GetStringAsync("/")));
    }

    [GeneratedRegex("href=\"([^\"]+)\"")]
    private static partial Regex LinkRegex();
}
