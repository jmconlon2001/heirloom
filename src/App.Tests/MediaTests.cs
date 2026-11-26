using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using FamilyMemories.Data;
using FamilyMemories.Media;
using FamilyMemories.Tests.Infrastructure;
using ImageMagick;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace FamilyMemories.Tests;

/// <summary>Helpers shared by the HTTP-level story tests.</summary>
public static class StoryClient
{
    /// <summary>Opens the first question for the signed-in storyteller; returns the memory id and the page's CSRF token.</summary>
    public static async Task<(Guid MemoryId, string Csrf, string Html)> OpenFirstQuestionAsync(Browser browser, AppFactory app, Guid storytellerId, string? key = "roots-born")
    {
        var promptId = await app.WithDbAsync(db => db.Prompts.Where(p => p.Key == key).Select(p => p.Id).SingleAsync());
        var (path, html, status) = await browser.GetAsync($"/answer/{promptId}");
        Assert.Equal(HttpStatusCode.OK, status);
        var memoryId = Guid.Parse(path.Split('/')[2]);
        return (memoryId, Browser.CsrfFromPage(html), html);
    }

    public static HttpRequestMessage Api(HttpMethod method, string url, string csrf, HttpContent? content = null)
    {
        var request = new HttpRequestMessage(method, url) { Content = content };
        request.Headers.Add("X-CSRF-TOKEN", csrf);
        return request;
    }

    /// <summary>Uploads like answer.js does: start, PUT chunks, complete. Returns the final response.</summary>
    public static async Task<HttpResponseMessage> UploadAsync(HttpClient http, string csrf, Guid memoryId, byte[] bytes, string name,
        bool voice = false, int chunk = 4 * 1024 * 1024)
    {
        var start = await http.SendAsync(Api(HttpMethod.Post, $"/api/memories/{memoryId}/uploads", csrf,
            JsonContent.Create(new { fileName = name, size = bytes.LongLength, voice })));
        if (!start.IsSuccessStatusCode) return start;
        var uploadId = (await start.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("uploadId").GetGuid();

        for (var offset = 0; offset < bytes.Length; offset += chunk)
        {
            var body = new ByteArrayContent(bytes, offset, Math.Min(chunk, bytes.Length - offset));
            body.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
            var put = await http.SendAsync(Api(HttpMethod.Put, $"/api/uploads/{uploadId}?offset={offset}", csrf, body));
            if (!put.IsSuccessStatusCode) return put;
        }
        return await http.SendAsync(Api(HttpMethod.Post, $"/api/uploads/{uploadId}/complete", csrf));
    }

    public static byte[] RotatedJpegWithGps()
    {
        // ImageMagick writes the EXIF orientation from this property, so set both.
        using var image = new MagickImage(MagickColors.SkyBlue, 300, 200) { Format = MagickFormat.Jpeg, Orientation = ImageMagick.Orientation.RightTop };
        var exif = new ExifProfile();
        exif.SetValue(ExifTag.Orientation, (ushort)6); // "rotate 90° clockwise to display"
        exif.SetValue(ExifTag.GPSLatitudeRef, "N");
        exif.SetValue(ExifTag.GPSLatitude, [new Rational(41, 1), new Rational(5, 1), new Rational(0, 1)]);
        image.SetProfile(exif);
        return image.ToByteArray();
    }
}

public sealed class MediaTests(PostgresFixture postgres) : IAsyncLifetime
{
    private readonly AppFactory app = new(postgres);
    public ValueTask InitializeAsync() => app.InitializeAsync();
    public ValueTask DisposeAsync() => app.DisposeAsync();

    private async Task<(AppUser User, Browser Browser, Guid MemoryId, string Csrf)> StorytellerAsync(string email)
    {
        var user = await app.CreateUserAsync("Rose", email, [Roles.Storyteller, Roles.Family], PromptSetKind.Core);
        var browser = await Browser.SignedInAsync(app, email);
        var (memoryId, csrf, _) = await StoryClient.OpenFirstQuestionAsync(browser, app, user.Id);
        return (user, browser, memoryId, csrf);
    }

    private Task<Attachment> AttachmentAsync(Guid id) => app.WithDbAsync(db => db.Attachments.SingleAsync(a => a.Id == id));

    private string PathOf(Attachment a, MediaVariant v) => app.Services.GetRequiredService<MediaStorage>().PathFor(a, v);

    private static async Task<Guid> IdFrom(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
    }

    [Fact]
    public async Task IPhone_heic_photo_becomes_web_jpegs_and_the_original_is_kept()
    {
        var (_, browser, memoryId, csrf) = await StorytellerAsync("heic@example.test");
        var bytes = await File.ReadAllBytesAsync(AppFactory.Fixture("photo.heic"));

        var id = await IdFrom(await StoryClient.UploadAsync(browser.Http, csrf, memoryId, bytes, "IMG_0001.HEIC"));
        var a = await AttachmentAsync(id);

        Assert.Equal(AttachmentKind.Photo, a.Kind);
        Assert.Equal(ProcessingState.Ready, a.State);
        Assert.Equal("image/heic", a.ContentType);
        Assert.Equal((640, 480), (a.Width!.Value, a.Height!.Value));
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(bytes)), a.Sha256);
        Assert.Equal(bytes, await File.ReadAllBytesAsync(PathOf(a, MediaVariant.Original)));

        using var web = new MagickImage(PathOf(a, MediaVariant.Web));
        Assert.Equal(MagickFormat.Jpeg, web.Format);
        using var thumb = new MagickImage(PathOf(a, MediaVariant.Thumb));
        Assert.Equal(400u, Math.Max(thumb.Width, thumb.Height));

        // Adding a photo counts as starting the story.
        Assert.Equal(MemoryStatus.Draft, await app.WithDbAsync(db => db.Memories.Where(m => m.Id == memoryId).Select(m => m.Status).SingleAsync()));
    }

    [Fact]
    public async Task Photos_are_turned_upright_and_location_is_stripped_from_copies_only()
    {
        var (_, browser, memoryId, csrf) = await StorytellerAsync("exif@example.test");
        var bytes = StoryClient.RotatedJpegWithGps();

        var a = await AttachmentAsync(await IdFrom(await StoryClient.UploadAsync(browser.Http, csrf, memoryId, bytes, "sideways.jpg")));

        Assert.Equal((200, 300), (a.Width!.Value, a.Height!.Value)); // rotated upright
        foreach (var variant in new[] { MediaVariant.Web, MediaVariant.Thumb })
        {
            using var copy = new MagickImage(PathOf(a, variant));
            Assert.True(copy.Height > copy.Width);
            Assert.Null(copy.GetExifProfile()); // no GPS, no EXIF at all
        }
        using var original = new MagickImage(PathOf(a, MediaVariant.Original));
        Assert.NotNull(original.GetExifProfile()?.GetValue(ExifTag.GPSLatitude)); // original untouched
    }

    [Theory]
    [InlineData("voice-safari.mp4")]
    [InlineData("voice-chrome.webm")]
    public async Task Voice_recordings_from_safari_and_chrome_become_m4a(string fixture)
    {
        var (_, browser, memoryId, csrf) = await StorytellerAsync($"voice-{fixture}@example.test");
        var bytes = await File.ReadAllBytesAsync(AppFactory.Fixture(fixture));

        var a = await AttachmentAsync(await IdFrom(await StoryClient.UploadAsync(browser.Http, csrf, memoryId, bytes, fixture, voice: true)));

        Assert.Equal(AttachmentKind.Audio, a.Kind);
        Assert.True(a.IsVoiceRecording);
        Assert.Equal("Voice recording", a.Caption);
        Assert.Equal(ProcessingState.Ready, a.State);
        Assert.InRange(a.DurationSeconds!.Value, 1.5, 2.5);
        var m4a = await File.ReadAllBytesAsync(PathOf(a, MediaVariant.Audio));
        Assert.Equal("ftyp", System.Text.Encoding.ASCII.GetString(m4a, 4, 4));

        // Safari needs range requests to play audio.
        var request = new HttpRequestMessage(HttpMethod.Get, $"/media/{a.Id}/audio");
        request.Headers.Range = new RangeHeaderValue(0, 99);
        var ranged = await browser.Http.SendAsync(request);
        Assert.Equal(HttpStatusCode.PartialContent, ranged.StatusCode);
        Assert.Equal("audio/mp4", ranged.Content.Headers.ContentType!.MediaType);
    }

    [Theory]
    [InlineData("video-android.webm")]      // VP9 + Opus: iPads can't play it as uploaded, so it's converted
    [InlineData("video-iphone.mp4")]        // already H.264 + AAC: repackaged, not re-encoded
    public async Task Videos_get_a_copy_every_ipad_can_play(string fixture)
    {
        var (_, browser, memoryId, csrf) = await StorytellerAsync($"video-{fixture}@example.test");
        var bytes = await File.ReadAllBytesAsync(AppFactory.Fixture(fixture));
        var a = await AttachmentAsync(await IdFrom(await StoryClient.UploadAsync(browser.Http, csrf, memoryId, bytes, fixture)));

        Assert.Equal(AttachmentKind.Video, a.Kind);
        Assert.Equal(ProcessingState.Ready, a.State);
        var mp4 = await File.ReadAllBytesAsync(PathOf(a, MediaVariant.Video));
        Assert.Equal("ftyp", System.Text.Encoding.ASCII.GetString(mp4, 4, 4));
        var text = System.Text.Encoding.ASCII.GetString(mp4);
        Assert.Contains("avc1", text);                                         // H.264
        Assert.Contains("mp4a", text);                                         // AAC sound
        Assert.True(text.IndexOf("moov", StringComparison.Ordinal) < text.IndexOf("mdat", StringComparison.Ordinal));   // streamable
        Assert.Equal(bytes, await File.ReadAllBytesAsync(PathOf(a, MediaVariant.Original)));   // original untouched

        var request = new HttpRequestMessage(HttpMethod.Get, $"/media/{a.Id}/video");
        request.Headers.Range = new RangeHeaderValue(0, 99);
        var ranged = await browser.Http.SendAsync(request);
        Assert.Equal(HttpStatusCode.PartialContent, ranged.StatusCode);
        Assert.Equal("video/mp4", ranged.Content.Headers.ContentType!.MediaType);
    }

    [Fact]
    public async Task Older_videos_play_from_the_original_until_the_hourly_job_converts_them()
    {
        var (_, browser, memoryId, csrf) = await StorytellerAsync("old-video@example.test");
        var bytes = await File.ReadAllBytesAsync(AppFactory.Fixture("video-android.webm"));
        var a = await AttachmentAsync(await IdFrom(await StoryClient.UploadAsync(browser.Http, csrf, memoryId, bytes, "old.webm")));
        File.Delete(PathOf(a, MediaVariant.Video));                           // as if uploaded before v0.9

        var fallback = await browser.Http.GetAsync($"/media/{a.Id}/video");
        Assert.Equal("video/webm", fallback.Content.Headers.ContentType!.MediaType);

        await app.WithServiceAsync<MediaProcessor, bool>(async p => { await p.BackfillPlayableVideosAsync(default); return true; });
        Assert.True(File.Exists(PathOf(a, MediaVariant.Video)));
        Assert.Equal("video/mp4", (await browser.Http.GetAsync($"/media/{a.Id}/video")).Content.Headers.ContentType!.MediaType);
    }

    [Fact]
    public async Task A_pdf_gets_a_picture_of_its_first_page()
    {
        var (_, browser, memoryId, csrf) = await StorytellerAsync("pdf-preview@example.test");
        var bytes = await File.ReadAllBytesAsync(AppFactory.Fixture("recipe.pdf"));
        var a = await AttachmentAsync(await IdFrom(await StoryClient.UploadAsync(browser.Http, csrf, memoryId, bytes, "recipe.pdf")));

        Assert.Equal(AttachmentKind.Document, a.Kind);
        Assert.Equal(ProcessingState.Ready, a.State);
        Assert.NotNull(a.Width);
        Assert.True(a.Height > a.Width);                                        // a portrait Letter page
        using var thumb = new MagickImage(PathOf(a, MediaVariant.Thumb));
        Assert.Equal(400u, thumb.Height);
        Assert.False(File.Exists(Path.Combine(Path.GetDirectoryName(PathOf(a, MediaVariant.Thumb))!, "page.jpg")));   // no leftovers

        var page = await browser.Http.GetStringAsync($"/stories/{memoryId}/write");
        Assert.Contains("alt=\"First page of recipe.pdf\"", page);
        Assert.Contains("Open recipe.pdf", page);
    }

    [Fact]
    public async Task An_uploaded_sound_only_mp4_is_treated_as_audio()
    {
        var (_, browser, memoryId, csrf) = await StorytellerAsync("mp4audio@example.test");
        var bytes = await File.ReadAllBytesAsync(AppFactory.Fixture("voice-safari.mp4"));
        var a = await AttachmentAsync(await IdFrom(await StoryClient.UploadAsync(browser.Http, csrf, memoryId, bytes, "memo.mp4")));
        Assert.Equal(AttachmentKind.Audio, a.Kind);
        Assert.False(a.IsVoiceRecording);
        Assert.True(File.Exists(PathOf(a, MediaVariant.Audio)));
    }

    [Fact]
    public async Task Chunked_upload_resumes_from_where_the_server_says()
    {
        var (_, browser, memoryId, csrf) = await StorytellerAsync("resume@example.test");
        var bytes = await File.ReadAllBytesAsync(AppFactory.Fixture("photo.jpg"));
        var start = await browser.Http.SendAsync(StoryClient.Api(HttpMethod.Post, $"/api/memories/{memoryId}/uploads", csrf,
            JsonContent.Create(new { fileName = "p.jpg", size = bytes.Length, voice = false })));
        var uploadId = (await start.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("uploadId").GetGuid();

        async Task<HttpResponseMessage> Put(int offset, int length) =>
            await browser.Http.SendAsync(StoryClient.Api(HttpMethod.Put, $"/api/uploads/{uploadId}?offset={offset}", csrf,
                new ByteArrayContent(bytes, offset, length)));

        Assert.Equal(HttpStatusCode.OK, (await Put(0, 5000)).StatusCode);

        // The connection "dropped" and the client lost track: the server tells it where to resume.
        var wrong = await Put(9000, 1000);
        Assert.Equal(HttpStatusCode.Conflict, wrong.StatusCode);
        Assert.Equal(5000, (await wrong.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("received").GetInt64());
        var status = await browser.Http.GetFromJsonAsync<JsonElement>($"/api/uploads/{uploadId}");
        Assert.Equal(5000, status.GetProperty("received").GetInt64());

        // Finishing early is refused; finishing properly works.
        var early = await browser.Http.SendAsync(StoryClient.Api(HttpMethod.Post, $"/api/uploads/{uploadId}/complete", csrf));
        Assert.Equal(HttpStatusCode.BadRequest, early.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Put(5000, bytes.Length - 5000)).StatusCode);
        var done = await browser.Http.SendAsync(StoryClient.Api(HttpMethod.Post, $"/api/uploads/{uploadId}/complete", csrf));
        var a = await AttachmentAsync(await IdFrom(done));
        Assert.Equal(bytes, await File.ReadAllBytesAsync(PathOf(a, MediaVariant.Original)));
    }

    [Fact]
    public async Task Files_are_judged_by_their_bytes_and_refused_early()
    {
        var (_, browser, memoryId, csrf) = await StorytellerAsync("refuse@example.test");
        var html = System.Text.Encoding.UTF8.GetBytes("<html><script>alert('hi')</script></html>");

        var response = await StoryClient.UploadAsync(browser.Http, csrf, memoryId, html, "holiday.jpg");
        Assert.Equal(HttpStatusCode.UnsupportedMediaType, response.StatusCode);
        Assert.Equal(0, await app.WithDbAsync(db => db.Attachments.CountAsync()));
        Assert.Equal(0, await app.WithDbAsync(db => db.UploadSessions.CountAsync()));
    }

    [Fact]
    public async Task Size_limits_apply_per_kind_of_file()
    {
        var (_, browser, memoryId, csrf) = await StorytellerAsync("toolarge@example.test");

        // Bigger than anything is allowed: refused before a single byte is sent.
        var huge = await browser.Http.SendAsync(StoryClient.Api(HttpMethod.Post, $"/api/memories/{memoryId}/uploads", csrf,
            JsonContent.Create(new { fileName = "huge.mov", size = 5L * 1024 * 1024 * 1024, voice = false })));
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, huge.StatusCode);

        // A 30 MB "photo" fits under the video limit but not the 25 MB image limit: refused on the first chunk.
        var start = await browser.Http.SendAsync(StoryClient.Api(HttpMethod.Post, $"/api/memories/{memoryId}/uploads", csrf,
            JsonContent.Create(new { fileName = "big.jpg", size = 30L * 1024 * 1024, voice = false })));
        var uploadId = (await start.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("uploadId").GetGuid();
        var jpegStart = (await File.ReadAllBytesAsync(AppFactory.Fixture("photo.jpg")))[..4096];
        var put = await browser.Http.SendAsync(StoryClient.Api(HttpMethod.Put, $"/api/uploads/{uploadId}?offset=0", csrf, new ByteArrayContent(jpegStart)));
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, put.StatusCode);
    }

    [Fact]
    public async Task Media_is_only_served_to_people_allowed_to_see_the_story()
    {
        var (_, browser, memoryId, csrf) = await StorytellerAsync("private-media@example.test");
        var id = await IdFrom(await StoryClient.UploadAsync(browser.Http, csrf, memoryId, await File.ReadAllBytesAsync(AppFactory.Fixture("photo.jpg")), "p.jpg"));

        Assert.Equal(HttpStatusCode.OK, (await browser.Http.GetAsync($"/media/{id}/thumb")).StatusCode);

        await app.CreateUserAsync("Cousin", "cousin-media@example.test", [Roles.Family]);
        var cousin = await Browser.SignedInAsync(app, "cousin-media@example.test");
        Assert.Equal(HttpStatusCode.NotFound, (await cousin.Http.GetAsync($"/media/{id}/thumb")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await cousin.Http.GetAsync($"/api/attachments/{id}")).StatusCode);

        var admin = await Browser.SignedInAsync(app, AppFactory.AdminEmail);
        Assert.Equal(HttpStatusCode.OK, (await admin.Http.GetAsync($"/media/{id}/web")).StatusCode);

        var anonymous = await new Browser(app).Http.GetAsync($"/media/{id}/thumb");
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
    }

    [Fact]
    public async Task A_removed_photo_is_only_served_to_the_people_who_could_put_it_back()
    {
        var (_, browser, memoryId, csrf) = await StorytellerAsync("removed-media@example.test");
        var id = await IdFrom(await StoryClient.UploadAsync(browser.Http, csrf, memoryId, await File.ReadAllBytesAsync(AppFactory.Fixture("photo.jpg")), "p.jpg"));
        await app.WithDbAsync(async db =>
        {
            var m = await db.Memories.SingleAsync(x => x.Id == memoryId);
            m.Status = MemoryStatus.Complete;
            m.Visibility = Visibility.AllFamily;
            await db.SaveChangesAsync();
            return true;
        });
        await app.CreateUserAsync("Cousin", "cousin-removed@example.test", [Roles.Family]);
        var cousin = await Browser.SignedInAsync(app, "cousin-removed@example.test");
        Assert.Equal(HttpStatusCode.OK, (await cousin.Http.GetAsync($"/media/{id}/thumb")).StatusCode);

        await app.WithDbAsync(async db =>
        {
            (await db.Attachments.SingleAsync(a => a.Id == id)).RemovedAt = DateTime.UtcNow;
            await db.SaveChangesAsync();
            return true;
        });
        Assert.Equal(HttpStatusCode.NotFound, (await cousin.Http.GetAsync($"/media/{id}/thumb")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await browser.Http.GetAsync($"/media/{id}/thumb")).StatusCode);    // the storyteller can still restore it
    }

    [Fact]
    public async Task Scripts_must_send_the_antiforgery_token()
    {
        var (_, browser, memoryId, _) = await StorytellerAsync("csrf@example.test");
        var response = await browser.Http.PostAsJsonAsync($"/api/memories/{memoryId}/autosave", new { body = "sneaky" });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("", await app.WithDbAsync(db => db.Memories.Where(m => m.Id == memoryId).Select(m => m.Body).SingleAsync()));
    }
}

/// <summary>The production path: Hangfire picks up the processing job.</summary>
public sealed class HangfireTests(PostgresFixture postgres) : IAsyncLifetime
{
    private sealed class HangfireFactory(PostgresFixture postgres) : AppFactory(postgres)
    {
        protected override IDictionary<string, string?> Settings => new Dictionary<string, string?> { ["JOBS_ENABLED"] = "true" };
    }

    private readonly HangfireFactory app = new(postgres);
    public ValueTask InitializeAsync() => app.InitializeAsync();
    public ValueTask DisposeAsync() => app.DisposeAsync();

    [Fact]
    public async Task Uploads_are_processed_in_the_background_and_the_dashboard_is_admin_only()
    {
        var rose = await app.CreateUserAsync("Rose", "hangfire@example.test", [Roles.Storyteller, Roles.Family], PromptSetKind.Core);
        var browser = await Browser.SignedInAsync(app, "hangfire@example.test");
        var (memoryId, csrf, _) = await StoryClient.OpenFirstQuestionAsync(browser, app, rose.Id);

        var response = await StoryClient.UploadAsync(browser.Http, csrf, memoryId, await File.ReadAllBytesAsync(AppFactory.Fixture("photo.jpg")), "p.jpg");
        var id = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();

        var state = ProcessingState.Pending;
        for (var i = 0; i < 60 && state == ProcessingState.Pending; i++)
        {
            await Task.Delay(500);
            state = await app.WithDbAsync(db => db.Attachments.Where(a => a.Id == id).Select(a => a.State).SingleAsync());
        }
        Assert.Equal(ProcessingState.Ready, state);

        Assert.NotEqual(HttpStatusCode.OK, (await browser.Http.GetAsync("/admin/jobs")).StatusCode);
        var admin = await Browser.SignedInAsync(app, AppFactory.AdminEmail);
        Assert.Equal(HttpStatusCode.OK, (await admin.Http.GetAsync("/admin/jobs")).StatusCode);
    }
}

public sealed class PdfWithoutPopplerTests(PostgresFixture postgres) : IAsyncLifetime
{
    private sealed class NoPopplerFactory(PostgresFixture postgres) : AppFactory(postgres)
    {
        protected override IDictionary<string, string?> Settings => new Dictionary<string, string?> { ["PDFTOPPM_PATH"] = "/nonexistent/pdftoppm" };
    }

    private readonly NoPopplerFactory app = new(postgres);
    public ValueTask InitializeAsync() => app.InitializeAsync();
    public ValueTask DisposeAsync() => app.DisposeAsync();

    [Fact]
    public async Task Without_the_preview_tool_a_pdf_is_still_added_with_a_link()
    {
        var user = await app.CreateUserAsync("Rose", "pdf-nopoppler@example.test", [Roles.Storyteller], PromptSetKind.Core);
        var browser = await Browser.SignedInAsync(app, "pdf-nopoppler@example.test");
        var (memoryId, csrf, _) = await StoryClient.OpenFirstQuestionAsync(browser, app, user.Id);
        var response = await StoryClient.UploadAsync(browser.Http, csrf, memoryId, await File.ReadAllBytesAsync(AppFactory.Fixture("recipe.pdf")), "recipe.pdf");
        var id = Guid.Parse(System.Text.Json.JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.GetProperty("id").GetString()!);
        var a = await app.WithDbAsync(db => db.Attachments.SingleAsync(x => x.Id == id));
        Assert.Equal(ProcessingState.Ready, a.State);
        Assert.Null(a.Width);
        var page = await browser.Http.GetStringAsync($"/stories/{memoryId}/write");
        Assert.Contains("Open recipe.pdf", page);
        Assert.DoesNotContain("First page of", page);
    }
}
