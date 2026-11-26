using System.Net;
using FamilyMemories.Data;
using FamilyMemories.Media;
using FamilyMemories.Stories;
using FamilyMemories.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace FamilyMemories.Tests;

public sealed class StoryMarkupUnitTests
{
    private static string Html(string body) => string.Join("|", StoryMarkup.Parse(body).Select(b => b.Kind switch
    {
        StoryBlockKind.Media => $"media:{b.MediaId}:{b.Caption}",
        StoryBlockKind.BulletList or StoryBlockKind.NumberedList => $"{b.Kind}[{string.Join(",", b.Items)}]",
        _ => $"{b.Kind}:{b.Html}",
    }));

    [Fact]
    public void Plain_stories_read_exactly_as_before()
    {
        Assert.Equal("Paragraph:It was the fair.<br>Joe won.|Paragraph:We married in 1952.", Html("It was the fair.\nJoe won.\n\nWe married in 1952."));
    }

    [Fact]
    public void Formatting_marks_become_headings_lists_quotes_bold_italic_and_links()
    {
        var body = "## The fair\n\nIt was **so** hot, and *everyone* came.\n\n- tomatoes\n- a blue ribbon\n\n1. First\n2. Second\n\n> Then you'd better buy me a lemonade.\n\nSee [the fair's history](https://example.org/fair).";
        Assert.Equal(
            "Heading:The fair|Paragraph:It was <strong>so</strong> hot, and <em>everyone</em> came.|BulletList[tomatoes,a blue ribbon]|NumberedList[First,Second]"
            + "|Quote:Then you&#39;d better buy me a lemonade.|Paragraph:See <a href=\"https://example.org/fair\" rel=\"noopener\">the fair&#39;s history</a>.",
            Html(body));
    }

    [Theory]
    [InlineData("<script>alert(1)</script>", "Paragraph:&lt;script&gt;alert(1)&lt;/script&gt;")]
    [InlineData("[click](javascript:alert(1))", "Paragraph:[click](javascript:alert(1))")]
    [InlineData("<img src=x onerror=alert(1)>", "Paragraph:&lt;img src=x onerror=alert(1)&gt;")]
    [InlineData("**<b>hi</b>**", "Paragraph:<strong>&lt;b&gt;hi&lt;/b&gt;</strong>")]
    [InlineData("[x](https://e.org/\"onmouseover=alert(1))", "Paragraph:[x](https://e.org/&quot;onmouseover=alert(1))")]
    [InlineData("2 * 3 * 4 = 24", "Paragraph:2 * 3 * 4 = 24")]
    public void Nothing_unsafe_gets_through(string body, string expected) => Assert.Equal(expected, Html(body));

    [Fact]
    public void Media_lines_place_photos_where_they_sit()
    {
        var id = Guid.NewGuid();
        var body = $"Before.\n\n{StoryMarkup.MediaMarker(id, "Joe [and] me")}\n\nAfter.";
        // A "]" in the caption would end the marker early, so it's swapped for ")".
        Assert.Equal($"Paragraph:Before.|media:{id}:Joe [and) me|Paragraph:After.", Html(body));
        Assert.Equal([id], StoryMarkup.PlacedMedia(body));
        Assert.Equal("Before.\n\nAfter.", StoryMarkup.PlainText(body));
    }

    [Fact]
    public void Excerpts_and_plain_text_drop_the_marks()
    {
        Assert.Equal("The fair\n\nIt was so hot, see the history.", StoryMarkup.PlainText("## The fair\n\nIt was **so** hot, see [the history](https://e.org)."));
        Assert.Equal("Short and sweet.", StoryMarkup.Excerpt("*Short* and **sweet**."));
    }

    [Fact]
    public void Markdown_for_ebooks_keeps_our_marks_and_escapes_the_rest()
    {
        static string Esc(string s) => s.Replace("#", "\\#").Replace("_", "\\_");
        Assert.Equal("**Joe** won \\#1 at the *fair\\_1950* [here](https://e.org)", StoryMarkup.InlineMarkdown("**Joe** won #1 at the *fair_1950* [here](https://e.org)", Esc));
    }
}

public sealed class StoryMarkupPageTests(PostgresFixture postgres) : IAsyncLifetime
{
    private readonly AppFactory app = new(postgres);
    public ValueTask InitializeAsync() => app.InitializeAsync();
    public ValueTask DisposeAsync() => app.DisposeAsync();

    [Fact]
    public async Task A_placed_photo_shows_inside_the_story_and_not_again_below_it()
    {
        var rose = await app.CreateUserAsync("Rose Smith", "markup-rose@example.test", [Roles.Storyteller, Roles.Family], PromptSetKind.Core);
        Guid memoryId, photoId, voiceId;
        await using (var scope = app.Services.CreateAsyncScope())
        {
            var memories = scope.ServiceProvider.GetRequiredService<MemoryService>();
            var attachments = scope.ServiceProvider.GetRequiredService<AttachmentService>();
            var m = await memories.CreateFreeFormAsync(rose.Id, rose.Id);
            memoryId = m.Id;
            await using (var s = File.OpenRead(AppFactory.Fixture("photo.jpg"))) photoId = (await attachments.AddFromStreamAsync(m.Id, "photo.jpg", s, rose.Id)).Item1!.Id;
            await using (var s = File.OpenRead(AppFactory.Fixture("voice-safari.mp4"))) voiceId = (await attachments.AddFromStreamAsync(m.Id, "voice.mp4", s, rose.Id)).Item1!.Id;
            await memories.SaveAsync(m.Id, new MemoryEdit("The county fair",
                $"## Summer 1950\n\nIt was **so** hot.\n\n{StoryMarkup.MediaMarker(photoId, "Joe and me at the fair")}\n\nJoe won a ribbon.", default, null), rose.Id, VersionKind.Save);
            await memories.SetStatusAsync(m.Id, MemoryStatus.Complete, rose.Id);
        }

        var browser = await Browser.SignedInAsync(app, "markup-rose@example.test");
        var html = WebUtility.HtmlDecode(await browser.Http.GetStringAsync($"/stories/{memoryId}"));
        var body = html[html.IndexOf("class=\"story-body\"")..];
        Assert.Contains("<h2 class=\"story-heading\">Summer 1950</h2>", body);
        Assert.Contains("<strong>so</strong>", body);
        var photoAt = body.IndexOf($"/media/{photoId}/web");
        Assert.True(photoAt > body.IndexOf("It was") && photoAt < body.IndexOf("Joe won a ribbon"), "the photo sits between the paragraphs");
        Assert.Contains("<figcaption>Joe and me at the fair</figcaption>", body);
        var gallery = html.IndexOf("class=\"gallery\"");
        Assert.True(gallery > 0, "the recording that wasn't placed still shows below");
        Assert.DoesNotContain($"/media/{photoId}/", html[gallery..]);
        Assert.Contains($"/media/{voiceId}/audio", html[gallery..]);

        var library = WebUtility.HtmlDecode(await browser.Http.GetStringAsync("/library"));
        Assert.Contains("Summer 1950 It was so hot. Joe won a ribbon.", library);   // excerpt without the marks
        Assert.DoesNotContain("media:", library);
    }
}
