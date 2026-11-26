using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FamilyMemories.Data;
using FamilyMemories.Extras;
using FamilyMemories.Reminders;
using FamilyMemories.Tests.Infrastructure;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace FamilyMemories.Tests;

public sealed class EmailReplyParserTests
{
    private static InboundEmail Mail(string text, string to = "memories+AB12CD34@example.test", string subject = "Re: A question for you") =>
        new("1", "rose@example.test", [to], subject, text, []);

    [Fact]
    public void Finds_the_code_in_the_plus_address_or_the_text()
    {
        Assert.Equal("AB12CD34", EmailReplyParser.FindCode(Mail("hi")));
        Assert.Equal("ZX98YW76", EmailReplyParser.FindCode(Mail("Ref: zx98yw76", to: "memories@example.test")));
        Assert.Null(EmailReplyParser.FindCode(Mail("no code", to: "memories@example.test")));
    }

    [Fact]
    public void Keeps_only_what_the_person_wrote()
    {
        var text = """
            I was born in Akron in 1931.
            My mother named me after her rose bush.

            Sent from my iPad

            On Sun, Nov 29, 2026 at 10:00 AM Family Memories <memories@example.test> wrote:
            > Here is this week's question:
            > Where and when were you born?
            """;
        Assert.Equal("I was born in Akron in 1931.\nMy mother named me after her rose bush.", EmailReplyParser.NewText(text));
        Assert.Equal("Short answer.", EmailReplyParser.NewText("Short answer.\n-- \nRose Smith\n555-0100"));
        Assert.Equal("Top.", EmailReplyParser.NewText("Top.\n> quoted\n> more"));
    }
}

public sealed class WhisperTranscriberTests
{
    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public HttpRequestMessage? Last { get; private set; }
        public string? Body { get; private set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Last = request;
            Body = request.Content is null ? null : await request.Content.ReadAsStringAsync(ct);
            return respond(request);
        }
    }

    private sealed class Factory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler);
    }

    [Fact]
    public async Task Sends_the_recording_to_the_local_whisper_service_and_reads_the_text()
    {
        var stub = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(new { text = " I was born in Akron. " }) });
        var options = new FamilyMemories.Configuration.AppOptions { WhisperUrl = "http://whisper:9000" };
        var file = Path.GetTempFileName();
        await File.WriteAllBytesAsync(file, [1, 2, 3]);
        var text = await new WhisperTranscriber(new Factory(stub), options).TranscribeAsync(file, default);
        Assert.Equal("I was born in Akron.", text);
        Assert.Equal("http://whisper:9000/asr?task=transcribe&language=en&output=json&encode=true", stub.Last!.RequestUri!.ToString());
        Assert.Contains("name=audio_file", stub.Body);
        File.Delete(file);
    }

    [Fact]
    public async Task A_url_ending_in_v1_talks_to_an_OpenAI_compatible_server_like_whisper_cpp_on_a_mac()
    {
        var stub = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(new { text = " It was a red wool coat. " }) });
        var options = new FamilyMemories.Configuration.AppOptions { WhisperUrl = "http://mac-mini.lan:8178/v1/", WhisperModel = "large-v3-turbo" };
        var file = Path.GetTempFileName();
        await File.WriteAllBytesAsync(file, [1, 2, 3]);
        var text = await new WhisperTranscriber(new Factory(stub), options).TranscribeAsync(file, default);
        Assert.Equal("It was a red wool coat.", text);
        Assert.Equal("http://mac-mini.lan:8178/v1/audio/transcriptions", stub.Last!.RequestUri!.ToString());
        Assert.Contains("name=file", stub.Body);
        Assert.Contains("large-v3-turbo", stub.Body);
        Assert.Contains("name=response_format", stub.Body);
        File.Delete(file);
    }
}

public sealed class TranscriptionTests(PostgresFixture postgres) : IAsyncLifetime
{
    private sealed class FakeTranscriber : ITranscriber
    {
        public Task<string> TranscribeAsync(string audioPath, CancellationToken ct) => Task.FromResult("I was born in a farmhouse outside Akron.");
    }

    private sealed class TranscriptionFactory(PostgresFixture postgres) : AppFactory(postgres)
    {
        protected override IDictionary<string, string?> Settings => new Dictionary<string, string?> { ["FEATURE_TRANSCRIPTION"] = "true", ["WHISPER_URL"] = "http://whisper:9000" };
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.ConfigureTestServices(s => { s.RemoveAll<ITranscriber>(); s.AddScoped<ITranscriber, FakeTranscriber>(); });
        }
    }

    private readonly TranscriptionFactory app = new(postgres);
    public ValueTask InitializeAsync() => app.InitializeAsync();
    public ValueTask DisposeAsync() => app.DisposeAsync();

    [Fact]
    public async Task A_voice_recording_is_written_down_and_added_only_when_the_storyteller_agrees()
    {
        var rose = await app.CreateUserAsync("Rose", "stt@example.test", [Roles.Storyteller], PromptSetKind.Core);
        var browser = await Browser.SignedInAsync(app, "stt@example.test");
        var (memoryId, csrf, _) = await StoryClient.OpenFirstQuestionAsync(browser, app, rose.Id);
        var upload = await StoryClient.UploadAsync(browser.Http, csrf, memoryId, await File.ReadAllBytesAsync(AppFactory.Fixture("voice-chrome.webm")), "voice.webm", voice: true);
        var attachmentId = (await upload.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();

        var a = await app.WithDbAsync(db => db.Attachments.SingleAsync(x => x.Id == attachmentId));
        Assert.Equal(TranscriptState.Ready, a.TranscriptState);
        Assert.Equal("", await app.WithDbAsync(db => db.Memories.Where(m => m.Id == memoryId).Select(m => m.Body).SingleAsync())); // not added yet

        var page = WebUtility.HtmlDecode(await browser.Http.GetStringAsync($"/stories/{memoryId}/write"));
        Assert.Contains("Here's what we heard", page);
        Assert.Contains("I was born in a farmhouse outside Akron.", page);

        var form = new MultipartFormDataContent
        {
            { new StringContent(HtmlForms.AntiforgeryToken(page)), "__RequestVerificationToken" },
            { new StringContent("My first words."), "Body" },
            { new StringContent("I was born in a farmhouse outside Akron, Ohio."), $"Transcripts[{attachmentId}]" },
            { new StringContent(attachmentId.ToString()), "attachmentId" },
        };
        await browser.Http.PostAsync($"/stories/{memoryId}/write?handler=UseTranscript", form);
        Assert.Equal("My first words.\n\nI was born in a farmhouse outside Akron, Ohio.",
            await app.WithDbAsync(db => db.Memories.Where(m => m.Id == memoryId).Select(m => m.Body).SingleAsync()));
        Assert.Equal(TranscriptState.Approved, await app.WithDbAsync(db => db.Attachments.Where(x => x.Id == attachmentId).Select(x => x.TranscriptState).SingleAsync()));

        // What speech-to-text heard, before the fix, is kept as raw material next to the story (D-079).
        var raw = await app.WithDbAsync(db => db.MemorySources.SingleAsync(x => x.MemoryId == memoryId));
        Assert.Equal((SourceKind.VoiceTranscript, "I was born in a farmhouse outside Akron.", (Guid?)attachmentId), (raw.Kind, raw.Text, raw.AttachmentId));
    }
}

public sealed class EmailReplyTests(PostgresFixture postgres) : IAsyncLifetime
{
    private sealed class FakeInbox : IReplyInbox
    {
        public List<InboundEmail> Waiting { get; } = [];
        public List<string> Done { get; } = [];
        public Task<IReadOnlyList<InboundEmail>> FetchUnreadAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<InboundEmail>>([.. Waiting.Where(w => !Done.Contains(w.Id))]);
        public Task MarkDoneAsync(string id, CancellationToken ct) { Done.Add(id); return Task.CompletedTask; }
    }

    private sealed class RepliesFactory(PostgresFixture postgres) : AppFactory(postgres)
    {
        public FakeInbox Inbox { get; } = new();
        protected override IDictionary<string, string?> Settings => new Dictionary<string, string?>
        {
            ["FEATURE_EMAIL_REPLIES"] = "true", ["REPLY_ADDRESS"] = "memories@example.test",
        };
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.ConfigureTestServices(s => { s.RemoveAll<IReplyInbox>(); s.AddSingleton<IReplyInbox>(Inbox); });
        }
    }

    private readonly RepliesFactory app = new(postgres);

    public async ValueTask InitializeAsync()
    {
        app.Clock.Set(new DateTimeOffset(2026, 11, 24, 17, 0, 0, TimeSpan.Zero));
        await app.InitializeAsync();
    }

    public ValueTask DisposeAsync() => app.DisposeAsync();

    [Fact]
    public async Task Replying_to_a_reminder_with_words_and_a_photo_makes_a_draft_to_check()
    {
        var rose = await app.CreateUserAsync("Rose", "reply-rose@example.test", [Roles.Storyteller], PromptSetKind.Core, Pacing.AllAtOnce);
        app.Clock.Set(new DateTimeOffset(2026, 11, 29, 15, 5, 0, TimeSpan.Zero)); // Sunday 10:05 New York
        await app.WithServiceAsync<ReminderService, TickResult>(s => s.TickAsync());
        var reminder = app.Email.To("reply-rose@example.test").Single();
        Assert.Matches(@"^memories\+[A-Z2-9]{8}@example\.test$", reminder.ReplyTo);
        Assert.Contains("reply to this email", reminder.TextBody);

        app.Inbox.Waiting.Add(new InboundEmail("m1", "Reply-Rose@Example.test", [reminder.ReplyTo!], "Re: " + reminder.Subject,
            "I was born at home, in the front bedroom.\n\nOn Sun, Nov 29 Family Memories wrote:\n> Here is this week's question",
            [("house.jpg", await File.ReadAllBytesAsync(AppFactory.Fixture("photo.jpg")))]));
        // Someone else trying to add to Rose's story is ignored.
        app.Inbox.Waiting.Add(new InboundEmail("m2", "stranger@example.test", [reminder.ReplyTo!], "Re: hi", "Spam", []));

        var result = await app.WithServiceAsync<EmailReplyProcessor, ReplyResult>(p => p.PollAsync(default));
        Assert.Equal(new ReplyResult(1, 1), result);
        Assert.Equal(["m1", "m2"], app.Inbox.Done);

        var memory = await app.WithDbAsync(db => db.Memories.Include(m => m.Attachments).SingleAsync(m => m.StorytellerId == rose.Id));
        Assert.Equal("I was born at home, in the front bedroom.", memory.Body);
        Assert.Equal(MemoryStatus.Draft, memory.Status);
        Assert.NotNull(memory.EmailReplyAt);
        Assert.Single(memory.Attachments);
        var raw = await app.WithDbAsync(db => db.MemorySources.SingleAsync(x => x.MemoryId == memory.Id));
        Assert.Equal(SourceKind.EmailReply, raw.Kind);
        Assert.Contains("> Here is this week's question", raw.Text);   // the whole reply, as it arrived

        var browser = await Browser.SignedInAsync(app, "reply-rose@example.test");
        var page = WebUtility.HtmlDecode(await browser.Http.GetStringAsync($"/stories/{memory.Id}/write"));
        Assert.Contains("We added what you sent by email", page);
        var admin = await Browser.SignedInAsync(app, AppFactory.AdminEmail);
        Assert.Contains("Answers sent by email, waiting for a look", await admin.Http.GetStringAsync("/admin"));

        await browser.Http.PostAsync($"/stories/{memory.Id}/write?handler=Finish", new MultipartFormDataContent
        {
            { new StringContent(HtmlForms.AntiforgeryToken(page)), "__RequestVerificationToken" },
            { new StringContent(memory.Body), "Body" },
        });
        var finished = await app.WithDbAsync(db => db.Memories.SingleAsync(m => m.Id == memory.Id));
        Assert.Equal(MemoryStatus.Complete, finished.Status);
        Assert.Null(finished.EmailReplyAt);
        Assert.True(await app.WithDbAsync(db => db.AuditLog.AnyAsync(a => a.Action == "email.reply-rejected")));
    }
}

public sealed class PaperPacketTests(PostgresFixture postgres, GotenbergFixture gotenberg) : IAsyncLifetime
{
    private sealed class PacketFactory(PostgresFixture postgres, GotenbergFixture gotenberg, bool on) : AppFactory(postgres)
    {
        protected override IDictionary<string, string?> Settings => new Dictionary<string, string?>
        {
            ["GOTENBERG_URL"] = gotenberg.Url, ["FEATURE_PAPER_PACKETS"] = on ? "true" : "false",
        };
    }

    private readonly PacketFactory app = new(postgres, gotenberg, on: true);
    public ValueTask InitializeAsync() => app.InitializeAsync();
    public ValueTask DisposeAsync() => app.DisposeAsync();

    [Fact]
    public async Task An_admin_prints_a_packet_with_a_letter_and_one_page_per_question()
    {
        var rose = await app.CreateUserAsync("Rose Smith", "packet@example.test", [Roles.Storyteller], PromptSetKind.Core, Pacing.AllAtOnce);
        var admin = await Browser.SignedInAsync(app, AppFactory.AdminEmail);
        var page = await admin.Http.GetStringAsync($"/admin/users/{rose.Id}");
        Assert.Contains("Print a paper packet of questions", page);
        var response = await admin.Http.PostFormAsync($"/admin/users/{rose.Id}/packet", page);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/pdf", response.Content.Headers.ContentType!.MediaType);
        var pdf = await response.Content.ReadAsByteArrayAsync();
        Assert.Equal(1 + PaperPackets.MaxQuestions, FamilyMemories.Exports.GotenbergClient.CountPages(pdf));
        Assert.True(await app.WithDbAsync(db => db.AuditLog.AnyAsync(a => a.Action == "packet.print")));
        Assert.Contains("Optional extras", await admin.Http.GetStringAsync("/admin"));
    }
}
