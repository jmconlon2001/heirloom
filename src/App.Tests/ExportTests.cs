using System.IO.Compression;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using FamilyMemories.Data;
using FamilyMemories.Exports;
using FamilyMemories.Family;
using FamilyMemories.Media;
using FamilyMemories.Stories;
using FamilyMemories.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace FamilyMemories.Tests;

public sealed class ExportMathTests
{
    [Fact]
    public void Spine_width_is_pages_divided_by_pages_per_inch()
    {
        Assert.Equal(0.4504, SpineCalculator.SpineWidthInches(200, SpineCalculator.Presets["kdp-white"].PagesPerInch), 3);
        Assert.Equal(0.5, SpineCalculator.SpineWidthInches(200, SpineCalculator.Presets["kdp-cream"].PagesPerInch), 3);
        Assert.Equal(0, SpineCalculator.SpineWidthInches(0, 444));
    }

    [Fact]
    public void Cover_is_back_plus_spine_plus_front_with_bleed()
    {
        var (w, h) = SpineCalculator.CoverInches(TrimSize.Size6x9, 200, 400);
        Assert.Equal(6 + 0.5 + 6 + 0.25, w, 3);
        Assert.Equal(9.25, h, 3);
        Assert.False(SpineCalculator.SpineCanHoldText(SpineCalculator.SpineWidthInches(60, 444)));
        Assert.True(SpineCalculator.SpineCanHoldText(SpineCalculator.SpineWidthInches(150, 444)));
    }

    [Theory]
    [InlineData(37, 2, 1)]
    [InlineData(38, 2, 0)]
    [InlineData(37, 4, 3)]
    [InlineData(37, 1, 0)]
    public void Pads_to_the_printers_multiple(int pages, int multiple, int padding) =>
        Assert.Equal(padding, SpineCalculator.PaddingPages(pages, multiple));

    [Fact]
    public void Effective_dpi_at_printed_size()
    {
        var book = new BookOptions { Trim = TrimSize.Size6x9 };
        Assert.Equal(4.5, Preflight.PrintedWidthInches(3000, 2000, book), 3);           // limited by the 4.5 in text block
        Assert.Equal(2.5, Preflight.PrintedWidthInches(1000, 2000, book), 3);           // tall photo, limited by 5 in height
        Assert.Equal(1350 / 4.5, Preflight.EffectiveDpi(1350, 900, book), 1);           // 300 dpi exactly
        Assert.True(Preflight.EffectiveDpi(640, 480, book) < 150);
    }

    [Fact]
    public void Tree_svg_draws_people_and_connections()
    {
        var joe = new Person { Id = Guid.NewGuid(), GivenNames = "Joe", Surname = "Smith", IsLiving = false, BirthYear = 1928, DeathYear = 2004 };
        var rose = new Person { Id = Guid.NewGuid(), GivenNames = "Rose", Surname = "Smith", BirthYear = 1931, BirthMonth = 6, BirthDay = 12 };
        var ann = new Person { Id = Guid.NewGuid(), GivenNames = "Ann", Surname = "Smith", BirthYear = 1956 };
        var svg = TreeSvg.Render([joe, rose, ann],
            [new() { ParentId = joe.Id, ChildId = ann.Id }, new() { ParentId = rose.Id, ChildId = ann.Id }],
            [new() { Person1Id = joe.Id, Person2Id = rose.Id }]);
        Assert.StartsWith("<svg", svg);
        Assert.Contains("Joe Smith", svg);
        Assert.Contains("1928 – 2004", svg);
        Assert.Contains("Born 1931", svg);           // living: year only, even in exports
        Assert.DoesNotContain("June", svg);
        Assert.Contains("<path", svg);               // parent → child
        Assert.Contains("<line", svg);               // the couple
    }
}

public sealed partial class ExportTests(PostgresFixture postgres, GotenbergFixture gotenberg) : IAsyncLifetime
{
    private sealed class ExportFactory(PostgresFixture postgres, GotenbergFixture gotenberg) : AppFactory(postgres)
    {
        protected override IDictionary<string, string?> Settings => new Dictionary<string, string?> { ["GOTENBERG_URL"] = gotenberg.Url };
    }

    private readonly ExportFactory app = new(postgres, gotenberg);
    public ValueTask InitializeAsync() => app.InitializeAsync();
    public ValueTask DisposeAsync() => app.DisposeAsync();

    private sealed record Seeded(AppUser Rose, Guid PublicStory, Guid PrivateStory, Guid VoiceId);

    private async Task<Seeded> SeedAsync()
    {
        var rose = await app.CreateUserAsync("Rose Smith", "export-rose@example.test", [Roles.Storyteller, Roles.Family], PromptSetKind.Core);
        await using var scope = app.Services.CreateAsyncScope();
        var sp = scope.ServiceProvider;
        var memories = sp.GetRequiredService<MemoryService>();
        var attachments = sp.GetRequiredService<AttachmentService>();
        var people = sp.GetRequiredService<PeopleService>();
        var sharing = sp.GetRequiredService<SharingService>();
        var db = sp.GetRequiredService<AppDbContext>();

        var rosePerson = await people.CreateAsync(new PersonInput("Rose", "Smith", "Miller", null, Sex.Female, new FuzzyDate(1931, 6, 12, false, null, null), null, true, default, null, null, rose.Id, null));
        var joe = await people.CreateAsync(new PersonInput("Joe", "Smith", null, "Grandpa Joe", Sex.Male, new FuzzyDate(1928, null, null, false, null, null), null, false, new FuzzyDate(2004, null, null, false, null, null), null, null, null, null));
        await people.AddPartnerAsync(rosePerson.Id, joe.Id, PartnershipKind.Married, 1952, null);

        async Task<Guid> Story(string key, string body, Visibility visibility, params string[] fixtures)
        {
            var prompt = await db.Prompts.SingleAsync(p => p.Key == key);
            var m = await memories.GetOrCreateForPromptAsync(rose.Id, prompt, rose.Id);
            await memories.SaveAsync(m.Id, new MemoryEdit(null, body, new FuzzyDate(1950, null, null, true, null, null), "Akron"), rose.Id, VersionKind.Save);
            foreach (var f in fixtures)
            {
                await using var s = File.OpenRead(AppFactory.Fixture(f));
                var (a, _) = await attachments.AddFromStreamAsync(m.Id, f, s, rose.Id);
                if (f.EndsWith(".jpg")) await attachments.SetCaptionAsync(m.Id, a!.Id, "Joe and me at the fair");
                if (f.StartsWith("photo")) await people.TagPhotoAsync(a!.Id, joe.Id, true);
            }
            await people.SetMemoryPeopleAsync(m.Id, [joe.Id]);
            await sharing.SetVisibilityAsync(m.Id, visibility, []);
            await memories.SetStatusAsync(m.Id, MemoryStatus.Complete, rose.Id);
            return m.Id;
        }

        var pub = await Story("love-meeting", "It was the county fair in 1950.\n\nJoe won a ribbon for his tomatoes and gave it to me.", Visibility.AllFamily, "photo.jpg", "photo.heic", "voice-safari.mp4");
        var priv = await Story("roots-born", "A private memory nobody else should read.", Visibility.Private);
        var voice = await db.Attachments.Where(a => a.MemoryId == pub && a.Kind == AttachmentKind.Audio).Select(a => a.Id).SingleAsync();
        return new Seeded(rose, pub, priv, voice);
    }

    private async Task<ExportJob> RunAsync(ExportKind kind, ExportScope? scope = null, BookOptions? book = null)
    {
        var adminId = await app.WithDbAsync(db => db.Users.Where(u => u.Email == AppFactory.AdminEmail).Select(u => u.Id).SingleAsync());
        var job = await app.WithServiceAsync<ExportService, ExportJob>(s => s.StartAsync(kind, scope ?? new ExportScope(), book ?? new BookOptions { Title = "The Smith Family" }, adminId));
        var done = await app.WithDbAsync(db => db.Exports.SingleAsync(e => e.Id == job.Id));
        Assert.True(done.Status == ExportStatus.Done, $"{kind} export: {done.Status}: {done.Message}");
        return done;
    }

    private string PathOf(ExportJob job) => Path.Combine(app.Services.GetRequiredService<ExportPipeline>().ExportsDir, job.FileName!);

    private static (double W, double H) FirstPageSizePoints(byte[] pdf)
    {
        var m = MediaBoxRegex().Match(System.Text.Encoding.Latin1.GetString(pdf));
        Assert.True(m.Success, "no MediaBox in PDF");
        double D(int i) => double.Parse(m.Groups[i].Value, System.Globalization.CultureInfo.InvariantCulture);
        return (D(3) - D(1), D(4) - D(2));
    }

    [Fact]
    public async Task Scope_leaves_out_private_and_unfinished_stories_unless_asked()
    {
        var s = await SeedAsync();
        var draft = await app.WithServiceAsync<MemoryService, Memory>(m => m.CreateFreeFormAsync(s.Rose.Id, s.Rose.Id));
        await app.WithServiceAsync<MemoryService, SaveResult>(m => m.SaveAsync(draft.Id, new MemoryEdit("Half written", "Draft", default, null), s.Rose.Id, VersionKind.Save));

        async Task<List<Guid>> Ids(ExportScope scope) => await app.WithServiceAsync<ExportContentBuilder, List<Guid>>(b => b.Query(scope).Select(m => m.Id).ToListAsync());
        Assert.Equal([s.PublicStory], await Ids(new ExportScope()));
        Assert.Equal(2, (await Ids(new ExportScope { IncludePrivate = true })).Count);
        Assert.Contains(draft.Id, await Ids(new ExportScope { Statuses = [MemoryStatus.Complete, MemoryStatus.Draft] }));
        Assert.Empty(await Ids(new ExportScope { StorytellerIds = [Guid.NewGuid()] }));
    }

    [Fact]
    public async Task Shareable_pdf_has_bookmarks_and_qr_codes_that_play_the_recording()
    {
        var s = await SeedAsync();
        var job = await RunAsync(ExportKind.Pdf);
        var pdf = await File.ReadAllBytesAsync(PathOf(job));
        Assert.Equal("%PDF", System.Text.Encoding.ASCII.GetString(pdf, 0, 4));
        var pages = GotenbergClient.CountPages(pdf);
        Assert.InRange(pages, 5, 40);                                   // title, contents, divider, story, tree, index…
        Assert.Contains("/Outlines", System.Text.Encoding.Latin1.GetString(pdf)); // clickable bookmarks
        var (w, h) = FirstPageSizePoints(pdf);
        Assert.Equal((612, 792), ((int)Math.Round(w), (int)Math.Round(h)));       // US Letter
        Assert.Matches(@"\d+ pages, 1 stories", job.Message);

        // The recording got a clip link (the QR code), which plays without an account.
        var clip = await app.WithDbAsync(db => db.LinkShares.SingleAsync(l => l.AttachmentId == s.VoiceId));
        Assert.NotNull(clip);
        Assert.Contains("Your PDF of the family's stories is ready", app.Email.To(AppFactory.AdminEmail).Last().Subject.Replace("Your ", "Your "));
    }

    [Fact]
    public async Task Book_package_has_everything_a_printer_needs_with_a_correct_spine()
    {
        await SeedAsync();
        var book = new BookOptions { Title = "The Smith Family", Subtitle = "Stories we told", Dedication = "For the grandchildren", Trim = TrimSize.Size6x9, PageMultiple = 4, PaperPreset = "kdp-cream", PagesPerInch = 400 };
        var job = await RunAsync(ExportKind.Book, book: book);

        using var zip = ZipFile.OpenRead(PathOf(job));
        var names = zip.Entries.Select(e => e.FullName).ToList();
        foreach (var expected in new[] { "interior.pdf", "cover.pdf", "book.epub", "book.docx", "preflight.txt", "source/book.md", "source/metadata.yaml", "source/people.ged" })
            Assert.Contains(expected, names);
        Assert.Contains(names, n => n.StartsWith("source/images/") && n.EndsWith(".jpg"));
        Assert.Contains(names, n => n.StartsWith("source/stories/") && n.EndsWith(".md"));

        byte[] Read(string name) { using var s = zip.GetEntry(name)!.Open(); using var ms = new MemoryStream(); s.CopyTo(ms); return ms.ToArray(); }
        var interior = Read("interior.pdf");
        var pages = GotenbergClient.CountPages(interior);
        Assert.Equal(0, pages % 4);                                           // padded to the printer's multiple
        var (iw, ih) = FirstPageSizePoints(interior);
        Assert.Equal((432, 648), ((int)Math.Round(iw), (int)Math.Round(ih)));  // 6 × 9 in

        var cover = Read("cover.pdf");
        var (cw, ch) = FirstPageSizePoints(cover);
        var expectedWidth = (6 + 6 + pages / 400.0 + 0.25) * 72;
        Assert.Equal(expectedWidth, cw, 0);                                   // back + spine + front + bleed
        Assert.Equal(9.25 * 72, ch, 0);

        var preflight = System.Text.Encoding.UTF8.GetString(Read("preflight.txt"));
        Assert.Contains($"Pages:         {pages}", preflight);
        Assert.Contains($"Spine width:   {pages / 400.0:0.###} in", preflight);
        Assert.Contains("PHOTOS BELOW 300 DPI AT PRINTED SIZE (2)", preflight); // the 640 px test photos
        Assert.Contains("EPUB and DOCX: made with pandoc.", preflight);
        var md = System.Text.Encoding.UTF8.GetString(Read("source/book.md"));
        Assert.Contains("Joe won a ribbon for his tomatoes", md);
        Assert.DoesNotContain("private memory", md);
        Assert.Contains("1 NAME Joe /Smith/", System.Text.Encoding.UTF8.GetString(Read("source/people.ged")));
        Assert.Equal("PK", System.Text.Encoding.ASCII.GetString(Read("book.epub"), 0, 2));
    }

    [Fact]
    public async Task Family_website_works_offline_with_relative_links_search_and_media()
    {
        var s = await SeedAsync();
        var job = await RunAsync(ExportKind.Website);
        using var zip = ZipFile.OpenRead(PathOf(job));
        var names = zip.Entries.Select(e => e.FullName).ToList();
        foreach (var expected in new[] { "index.html", "timeline.html", "photos.html", "people.html", "tree.html", "search.html", "assets/site.css", "assets/search.js", "assets/search-index.js" })
            Assert.Contains(expected, names);
        var storyPage = names.Single(n => n.StartsWith("story-"));
        string Read(string name) { using var r = new StreamReader(zip.GetEntry(name)!.Open()); return r.ReadToEnd(); }

        var html = Read(storyPage);
        Assert.Contains("Joe won a ribbon for his tomatoes", html);
        Assert.DoesNotContain("href=\"/", html);                       // no absolute links: works from file:// and any host
        Assert.DoesNotContain("src=\"/", html);
        Assert.DoesNotContain("type=\"module\"", Read("search.html"));  // modules don't load from file://
        foreach (Match m in SrcRegex().Matches(html))
            Assert.Contains(m.Groups[1].Value, names);                  // every image/audio it uses is in the zip
        Assert.Contains(names, n => n.EndsWith("/audio.m4a"));
        Assert.Contains("tomatoes", Read("assets/search-index.js"));
        Assert.DoesNotContain("private memory", string.Join("", names.Where(n => n.EndsWith(".html") || n.EndsWith(".js")).Select(Read)));
        Assert.Contains("Joe Smith", Read("tree.html"));
        Assert.True(s.PrivateStory != Guid.Empty);
    }

    [Fact]
    public async Task Portable_archive_keeps_everything_in_open_formats()
    {
        var s = await SeedAsync();
        var job = await RunAsync(ExportKind.Archive);
        using var zip = ZipFile.OpenRead(PathOf(job));
        var names = zip.Entries.Select(e => e.FullName).ToList();
        Assert.Contains("data.json", names);
        Assert.Contains("people.ged", names);
        Assert.Contains("README.txt", names);
        using var json = JsonDocument.Parse(zip.GetEntry("data.json")!.Open());
        var stories = json.RootElement.GetProperty("stories").EnumerateArray().ToList();
        Assert.Equal(2, stories.Count);                                 // the archive includes private stories too
        var withMedia = stories.Single(x => x.GetProperty("Id").GetGuid() == s.PublicStory);
        foreach (var a in withMedia.GetProperty("attachments").EnumerateArray())
            Assert.Contains(a.GetProperty("file").GetString(), names);  // every original is in the zip
        Assert.Equal(2, json.RootElement.GetProperty("family_tree").GetProperty("people").GetArrayLength());
    }

    [Fact]
    public async Task Admins_start_and_download_exports_and_private_needs_confirmation()
    {
        await SeedAsync();
        var admin = await Browser.SignedInAsync(app, AppFactory.AdminEmail);
        var page = await admin.Http.GetStringAsync("/admin/exports");
        var refused = await admin.Http.PostFormAsync("/admin/exports", page, ("Kind", "Website"), ("IncludePrivate", "true"));
        Assert.Contains("please also tick", WebUtility.HtmlDecode(await refused.Content.ReadAsStringAsync()));
        Assert.False(await app.WithDbAsync(db => db.Exports.AnyAsync()));

        await admin.Http.PostFormAsync("/admin/exports", page, ("Kind", "Website"), ("IncludePrivate", "true"), ("ConfirmPrivate", "true"));
        var job = await app.WithDbAsync(db => db.Exports.SingleAsync());
        Assert.Equal(ExportStatus.Done, job.Status);
        Assert.Contains("Download family-website-v1", WebUtility.HtmlDecode(await admin.Http.GetStringAsync("/admin/exports")));
        var download = await admin.Http.GetAsync($"/admin/exports/{job.Id}/download");
        Assert.Equal(HttpStatusCode.OK, download.StatusCode);
        Assert.Equal("application/zip", download.Content.Headers.ContentType!.MediaType);
        Assert.True(await app.WithDbAsync(db => db.AuditLog.AnyAsync(a => a.Action == "export.start" && a.Summary.Contains("including private"))));

        var rose = await Browser.SignedInAsync(app, "export-rose@example.test");
        Assert.NotEqual(HttpStatusCode.OK, (await rose.Http.GetAsync($"/admin/exports/{job.Id}/download")).StatusCode);
    }

    [GeneratedRegex(@"/MediaBox\s*\[\s*([\d.]+)\s+([\d.]+)\s+([\d.]+)\s+([\d.]+)\s*\]")]
    private static partial Regex MediaBoxRegex();

    [GeneratedRegex("(?:src|poster)=\"([^\"]+)\"")]
    private static partial Regex SrcRegex();
}
