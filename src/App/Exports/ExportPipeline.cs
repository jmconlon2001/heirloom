using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using FamilyMemories.Configuration;
using FamilyMemories.Data;
using FamilyMemories.Family;
using FamilyMemories.Media;
using Microsoft.EntityFrameworkCore;

namespace FamilyMemories.Exports;

/// <summary>Builds one export file. Progress is reported as (percent, plain-words message).</summary>
public sealed class ExportPipeline(
    AppDbContext db,
    ExportContentBuilder builder,
    RazorRenderer razor,
    GotenbergClient gotenberg,
    ExportImages images,
    MediaStorage storage,
    SharingService sharing,
    GedcomImporter gedcom,
    AppOptions options,
    ILogger<ExportPipeline> log)
{
    public static string AssetsDir => Path.Combine(AppContext.BaseDirectory, "Templates", "Export", "assets");
    public string ExportsDir => Path.Combine(options.DataDir, "exports");

    public static string FileNameFor(ExportKind kind, int version, DateTime at) => kind switch
    {
        ExportKind.Website => $"family-website-v{version}-{at:yyyy-MM-dd}.zip",
        ExportKind.Pdf => $"family-stories-v{version}-{at:yyyy-MM-dd}.pdf",
        ExportKind.Book => $"family-book-v{version}-{at:yyyy-MM-dd}.zip",
        _ => $"family-archive-v{version}-{at:yyyy-MM-dd}.zip",
    };

    public async Task<string> RunAsync(ExportJob job, ExportScope scope, BookOptions book, Func<int, string, Task> progress, CancellationToken ct)
    {
        Directory.CreateDirectory(ExportsDir);
        var work = Path.Combine(ExportsDir, $"work-{job.Id:N}");
        Directory.CreateDirectory(work);
        var output = Path.Combine(ExportsDir, FileNameFor(job.Kind, job.Version, job.CreatedAt));
        try
        {
            switch (job.Kind)
            {
                case ExportKind.Website: await WebsiteAsync(scope, output, progress, ct); break;
                case ExportKind.Pdf: await PdfAsync(job, scope, book, output, work, progress, ct); break;
                case ExportKind.Book: await BookAsync(job, scope, book, output, work, progress, ct); break;
                default: await ArchiveAsync(output, progress, ct); break;
            }
            return output;
        }
        finally
        {
            try { Directory.Delete(work, recursive: true); } catch (IOException) { }
        }
    }

    // ------------------------------------------------------------------ shared book rendering

    private Func<Attachment, Task<string?>> ClipLinks(ExportJob job) => async a =>
        sharing.LinkUrl(await sharing.CreateClipLinkAsync(a, job.CreatedById, $"QR code in {job.Kind} export v{job.Version}")).ToString();

    internal sealed record RenderedBook(byte[] Pdf, int Pages, int Padding, Dictionary<Guid, (int W, int H)> Sources);

    internal async Task<RenderedBook> RenderBookAsync(ExportContent content, BookOptions o, Medium medium, string work, CancellationToken ct)
    {
        var dir = Path.Combine(work, medium == Medium.PrintBook ? "interior" : "pdf");
        Directory.CreateDirectory(dir);
        File.Copy(Path.Combine(AssetsDir, "paged.polyfill.min.js"), Path.Combine(dir, "paged.polyfill.min.js"), true);
        var css = InlineCss.Load(AssetsDir, "book.css");

        var src = new Dictionary<Guid, string>();
        var sources = new Dictionary<Guid, (int W, int H)>();
        var maxPx = medium == Medium.PrintBook ? 3000 : 1600;
        foreach (var s in content.Stories)
        {
            foreach (var p in s.Photos)
            {
                src[p.Attachment.Id] = images.Prepare(p.Attachment, dir, maxPx).FileName;
                sources[p.Attachment.Id] = (p.Attachment.Width ?? 0, p.Attachment.Height ?? 0);
            }
            foreach (var c in s.Clips.Where(c => c.Attachment.Kind == AttachmentKind.Video))
                src[c.Attachment.Id] = images.Prepare(c.Attachment, dir, 1200, "poster").FileName;
        }
        var qr = new Dictionary<Guid, string>();
        foreach (var c in content.Stories.SelectMany(s => s.Clips).Where(c => c.ClipUrl is not null))
            qr[c.Attachment.Id] = ExportImages.Qr(c.ClipUrl!, dir, c.Attachment.Id);
        var tree = TreeSvg.Render(content.AllPeople, content.Parents, content.Partnerships);

        async Task<byte[]> Render(int blank)
        {
            var html = await razor.RenderAsync("/Templates/Export/Book.cshtml",
                new BookView(content, o, medium, a => src.GetValueOrDefault(a.Id, ""), c => qr.GetValueOrDefault(c.Attachment.Id), tree, blank, css));
            await File.WriteAllTextAsync(Path.Combine(dir, "index.html"), html, ct);
            return await gotenberg.HtmlToPdfAsync(dir, waitForPagedJs: true, outline: true, ct);
        }

        var pdf = await Render(0);
        var pages = GotenbergClient.CountPages(pdf);
        var padding = medium == Medium.PrintBook ? SpineCalculator.PaddingPages(pages, o.PageMultiple) : 0;
        if (padding > 0)
        {
            pdf = await Render(padding);
            pages = GotenbergClient.CountPages(pdf);
        }
        return new RenderedBook(pdf, pages, padding, sources);
    }

    // ------------------------------------------------------------------ PDF

    private async Task PdfAsync(ExportJob job, ExportScope scope, BookOptions o, string output, string work, Func<int, string, Task> progress, CancellationToken ct)
    {
        await progress(10, "Gathering the stories");
        var content = await builder.BuildAsync(scope, o.Organize, options.AppName, job.CreatedAt, ClipLinks(job));
        await progress(30, "Laying out the pages (this can take a few minutes)");
        var book = await RenderBookAsync(content, o, Medium.ScreenPdf, work, ct);
        await File.WriteAllBytesAsync(output, book.Pdf, ct);
        await progress(100, $"Done: {book.Pages} pages, {content.Stories.Count} stories.");
    }

    // ------------------------------------------------------------------ book package

    private async Task BookAsync(ExportJob job, ExportScope scope, BookOptions o, string output, string work, Func<int, string, Task> progress, CancellationToken ct)
    {
        await progress(5, "Gathering the stories");
        var content = await builder.BuildAsync(scope, o.Organize, options.AppName, job.CreatedAt, ClipLinks(job));
        var package = Path.Combine(work, "package");
        Directory.CreateDirectory(package);

        await progress(15, "Laying out the book's pages (this can take a few minutes)");
        var interior = await RenderBookAsync(content, o, Medium.PrintBook, work, ct);
        await File.WriteAllBytesAsync(Path.Combine(package, "interior.pdf"), interior.Pdf, ct);

        await progress(55, "Making the cover");
        var cover = await CoverAsync(content, o, interior.Pages, work, ct);
        await File.WriteAllBytesAsync(Path.Combine(package, "cover.pdf"), cover, ct);

        await progress(65, "Writing the source files");
        var source = Path.Combine(package, "source");
        await SourceAsync(content, o, source, ct);

        await progress(80, "Making the e-book and Word versions");
        var pandocNote = await PandocAsync(source, package, o, ct);

        await progress(90, "Checking for print problems");
        var chapters = await db.Chapters.AsNoTracking().OrderBy(c => c.SortOrder).ToListAsync(ct);
        var report = Preflight.Report(content, o, interior.Pages, interior.Padding, interior.Sources, chapters, scope, pandocNote);
        await File.WriteAllTextAsync(Path.Combine(package, "preflight.txt"), report, ct);

        await progress(95, "Packing it up");
        if (File.Exists(output)) File.Delete(output);
        ZipFile.CreateFromDirectory(package, output, CompressionLevel.Optimal, includeBaseDirectory: false);
        await progress(100, $"Done: {interior.Pages} pages, spine {SpineCalculator.SpineWidthInches(interior.Pages, o.PagesPerInch):0.###} in.");
    }

    private async Task<byte[]> CoverAsync(ExportContent content, BookOptions o, int pages, string work, CancellationToken ct)
    {
        var dir = Path.Combine(work, "cover");
        Directory.CreateDirectory(dir);

        var photos = content.Stories.SelectMany(s => s.Photos).Select(p => p.Attachment).ToList();
        string? Photo(Guid? id, string prefix) =>
            (id is { } g ? photos.FirstOrDefault(a => a.Id == g) : null) is { } a ? images.Prepare(a, dir, 2400, prefix).FileName : null;
        var front = Photo(o.FrontPhotoId ?? photos.FirstOrDefault()?.Id, "front");
        var back = Photo(o.BackPhotoId, "back");

        var (w, h) = SpineCalculator.CoverInches(o.Trim, pages, o.PagesPerInch);
        var spine = SpineCalculator.SpineWidthInches(pages, o.PagesPerInch);
        var html = await razor.RenderAsync("/Templates/Export/Cover.cshtml",
            new CoverView(o, w, h, spine, SpineCalculator.TrimInches(o.Trim).Width, front, back, SpineCalculator.SpineCanHoldText(spine), InlineCss.Load(AssetsDir, "cover.css")));
        await File.WriteAllTextAsync(Path.Combine(dir, "index.html"), html, ct);
        return await gotenberg.HtmlToPdfAsync(dir, waitForPagedJs: false, outline: false, ct);
    }

    /// <summary>Markdown per story, the whole book as one Markdown file, metadata, full-resolution images and the tree as GEDCOM.</summary>
    private async Task SourceAsync(ExportContent content, BookOptions o, string source, CancellationToken ct)
    {
        var imagesDir = Path.Combine(source, "images");
        var storiesDir = Path.Combine(source, "stories");
        Directory.CreateDirectory(imagesDir);
        Directory.CreateDirectory(storiesDir);

        var book = new StringBuilder();
        foreach (var section in content.Sections)
        {
            book.Append("# ").AppendLine(Md(section.Title)).AppendLine();
            foreach (var s in section.Stories)
            {
                var story = new StringBuilder();
                story.Append("## ").AppendLine(Md(s.Memory.Title)).AppendLine();
                story.Append('*').Append(Md($"Told by {s.Teller}{(s.WrittenBy is null ? "" : $", written down by {s.WrittenBy}")}{(s.When.Length > 0 ? $" · {s.When}" : "")}{(s.Memory.Place is null ? "" : $" · {s.Memory.Place}")}")).AppendLine("*").AppendLine();
                if (s.Question is not null) story.Append("> ").AppendLine(Md(s.Question)).AppendLine();
                foreach (var p in s.Paragraphs) story.AppendLine(string.Join("  \n", p.Select(Md))).AppendLine();
                foreach (var photo in s.Photos)
                {
                    var file = images.Prepare(photo.Attachment, imagesDir, 4000, "photo").FileName;
                    story.Append("![").Append(Md(photo.Caption ?? "")).Append("](images/").Append(file).AppendLine(")").AppendLine();
                }
                foreach (var clip in s.Clips.Where(c => c.ClipUrl is not null))
                    story.Append("[Listen or watch: ").Append(Md(clip.Caption ?? "recording")).Append("](").Append(clip.ClipUrl).AppendLine(")").AppendLine();
                book.Append(story);

                var front = $"---\ntitle: {Yaml(s.Memory.Title)}\nteller: {Yaml(s.Teller)}\ndate: {Yaml(s.When)}\nplace: {Yaml(s.Memory.Place ?? "")}\npeople: [{string.Join(", ", s.People.Select(p => Yaml(p.DisplayName)))}]\n---\n\n";
                await File.WriteAllTextAsync(Path.Combine(storiesDir, $"{s.Slug}.md"), front + story.ToString().Replace("](images/", "](../images/"), ct);
            }
        }
        await File.WriteAllTextAsync(Path.Combine(source, "book.md"), book.ToString(), ct);
        var meta = new StringBuilder()
            .AppendLine($"title: {Yaml(o.Title)}")
            .AppendLine($"subtitle: {Yaml(o.Subtitle ?? "")}")
            .AppendLine($"author: [{string.Join(", ", content.Stories.Select(s => s.Teller).Distinct().Select(Yaml))}]")
            .AppendLine($"date: {Yaml(content.CreatedAt.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture))}")
            .AppendLine("lang: en-US")
            .AppendLine($"rights: {Yaml($"© {content.CreatedAt.Year} the family")}");
        if (!string.IsNullOrWhiteSpace(o.Dedication)) meta.AppendLine($"dedication: {Yaml(o.Dedication)}");
        await File.WriteAllTextAsync(Path.Combine(source, "metadata.yaml"), meta.ToString(), ct);
        await File.WriteAllTextAsync(Path.Combine(source, "people.ged"), await gedcom.ExportAsync(), ct);
    }

    /// <summary>EPUB 3 and DOCX through pandoc. Returns a line for the preflight report.</summary>
    private async Task<string> PandocAsync(string source, string package, BookOptions o, CancellationToken ct)
    {
        try
        {
            var common = new List<string> { "book.md", "--metadata-file=metadata.yaml", "--toc", "--toc-depth=2", "--resource-path=." };
            var epubArgs = new List<string>(common) { "-o", Path.Combine(package, "book.epub") };
            if (Directory.GetFiles(Path.Combine(source, "images")).FirstOrDefault() is { } coverImage && o.FrontPhotoId is not null)
                epubArgs.Add($"--epub-cover-image={coverImage}");
            foreach (var args in new[] { epubArgs, new List<string>(common) { "-o", Path.Combine(package, "book.docx") } })
            {
                var result = await RunInAsync(source, "pandoc", args, ct);
                if (!result.Succeeded) return "EPUB/DOCX: pandoc failed: " + result.StdErr.Trim();
            }
            return "EPUB and DOCX: made with pandoc.";
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return "EPUB and DOCX: skipped, because pandoc isn't installed on this computer.";
        }
    }

    private static async Task<ToolResult> RunInAsync(string folder, string tool, IEnumerable<string> args, CancellationToken ct)
    {
        var psi = new System.Diagnostics.ProcessStartInfo(tool) { WorkingDirectory = folder, RedirectStandardError = true, RedirectStandardOutput = true, UseShellExecute = false };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = System.Diagnostics.Process.Start(psi)!;
        var err = p.StandardError.ReadToEndAsync(ct);
        await p.StandardOutput.ReadToEndAsync(ct);
        await p.WaitForExitAsync(ct);
        return new ToolResult(p.ExitCode, "", await err);
    }

    // ------------------------------------------------------------------ website

    private async Task WebsiteAsync(ExportScope scope, string output, Func<int, string, Task> progress, CancellationToken ct)
    {
        await progress(10, "Gathering the stories");
        var content = await builder.BuildAsync(scope, OrganizeBy.Storyteller, options.AppName, DateTime.UtcNow);
        if (File.Exists(output)) File.Delete(output);
        await using var file = File.Create(output);
        using var zip = new ZipArchive(file, ZipArchiveMode.Create);

        async Task Page(string name, string view, SitePage model)
        {
            var entry = zip.CreateEntry(name, CompressionLevel.Optimal);
            await using var w = new StreamWriter(entry.Open(), new UTF8Encoding(false));
            await w.WriteAsync(await razor.RenderAsync($"/Templates/Export/Site/{view}.cshtml", model));
        }

        await progress(30, "Writing the pages");
        await Page("index.html", "Index", new SitePage(content, "Stories", "stories"));
        await Page("timeline.html", "Timeline", new SitePage(content, "Timeline", "timeline"));
        await Page("photos.html", "Photos", new SitePage(content, "Photos", "photos"));
        await Page("people.html", "People", new SitePage(content, "People", "people"));
        await Page("search.html", "Search", new SitePage(content, "Search", "search"));
        if (content.AllPeople.Count > 0)
            await Page("tree.html", "Tree", new SitePage(content, "Family tree", "tree") { TreeSvg = TreeSvg.Render(content.AllPeople, content.Parents, content.Partnerships) });
        var playable = content.Stories.SelectMany(s => s.Clips).Select(c => c.Attachment)
            .Where(a => a.Kind == AttachmentKind.Video && File.Exists(storage.PathFor(a, MediaVariant.Video))).Select(a => a.Id).ToHashSet();
        foreach (var s in content.Stories) await Page(SitePage.StoryHref(s), "Story", new SitePage(content, s.Memory.Title, "stories") { Story = s, PlayableVideos = playable });
        foreach (var p in content.People) await Page(SitePage.PersonHref(p), "Person", new SitePage(content, p.Person.DisplayName, "people") { Person = p });

        foreach (var f in new[] { "site.css", "search.js" }) zip.CreateEntryFromFile(Path.Combine(AssetsDir, f), $"assets/{f}");
        foreach (var f in Directory.GetFiles(Path.Combine(AssetsDir, "fonts"), "*.woff2")) zip.CreateEntryFromFile(f, $"assets/fonts/{Path.GetFileName(f)}");
        var index = content.Stories.Select(s => new
        {
            title = s.Memory.Title, teller = s.Teller, url = SitePage.StoryHref(s),
            people = string.Join(" ", s.People.Select(p => p.DisplayName)),
            text = (s.Memory.Body + " " + (s.Memory.Place ?? "") + " " + string.Join(" ", s.Photos.Select(p => p.Caption))).Replace('\n', ' '),
        });
        var indexEntry = zip.CreateEntry("assets/search-index.js");
        await using (var w = new StreamWriter(indexEntry.Open(), new UTF8Encoding(false)))
            await w.WriteAsync("window.SEARCH_INDEX = " + JsonSerializer.Serialize(index) + ";");

        await progress(60, "Adding photos and recordings");
        void Add(Attachment a, MediaVariant v, string name)
        {
            var path = storage.PathFor(a, v);
            if (File.Exists(path)) zip.CreateEntryFromFile(path, SitePage.Media(a, name), CompressionLevel.NoCompression);
        }
        foreach (var s in content.Stories)
        {
            foreach (var p in s.Photos) { Add(p.Attachment, MediaVariant.Web, "web.jpg"); Add(p.Attachment, MediaVariant.Thumb, "thumb.jpg"); }
            foreach (var c in s.Clips)
            {
                if (c.Attachment.Kind == AttachmentKind.Video) { var converted = File.Exists(storage.PathFor(c.Attachment, MediaVariant.Video));
                    Add(c.Attachment, converted ? MediaVariant.Video : MediaVariant.Original, converted ? "video.mp4" : "video" + c.Attachment.Extension); Add(c.Attachment, MediaVariant.Poster, "poster.jpg"); }
                else Add(c.Attachment, MediaVariant.Audio, "audio.m4a");
            }
            foreach (var d in s.Documents) Add(d, MediaVariant.Original, "original" + d.Extension);
        }
        await progress(100, $"Done: {content.Stories.Count} stories.");
    }

    // ------------------------------------------------------------------ portable archive

    /// <summary>Everything, in open formats, independent of this app (§10): JSON, original media, GEDCOM, README.</summary>
    private async Task ArchiveAsync(string output, Func<int, string, Task> progress, CancellationToken ct)
    {
        await progress(10, "Collecting everything");
        var data = new
        {
            format = "family-memories-archive/1",
            exportedAt = DateTime.UtcNow,
            app = options.AppName,
            people_who_sign_in = await db.Users.AsNoTracking().Select(u => new { u.Id, u.DisplayName, u.Email, u.CreatedAt }).ToListAsync(ct),
            chapters = await db.Chapters.AsNoTracking().Select(c => new { c.Id, c.Title, c.SortOrder }).ToListAsync(ct),
            questions = await db.Prompts.AsNoTracking().Select(p => new { p.Id, p.ChapterId, p.Title, p.Question, p.Hints, p.ForUserId, p.AskedByUserId }).ToListAsync(ct),
            stories = await db.Memories.AsNoTracking().Select(m => new
            {
                m.Id, m.StorytellerId, m.WrittenById, m.PromptId, m.AboutPersonId, m.Title, m.Body, Status = m.Status.ToString(),
                Visibility = m.Visibility.ToString(), m.WhenYear, m.WhenMonth, m.WhenDay, m.WhenCirca, m.WhenDecade, m.WhenLabel, m.Place,
                m.CreatedAt, m.UpdatedAt, m.CompletedAt,
                versions = db.MemoryVersions.Where(v => v.MemoryId == m.Id).OrderBy(v => v.Number).Select(v => new { v.Number, v.Title, v.Body, v.SavedAt, v.SavedById }).ToList(),
                attachments = db.Attachments.Where(a => a.MemoryId == m.Id && a.RemovedAt == null).OrderBy(a => a.SortOrder).Select(a => new
                {
                    a.Id, Kind = a.Kind.ToString(), a.OriginalFileName, a.ContentType, a.Caption, a.Sha256, a.Width, a.Height, a.DurationSeconds,
                    file = "media/" + a.MemoryId.ToString().Replace("-", "") + "/" + a.Id.ToString().Replace("-", "") + "/original" + a.Extension,
                    people = db.AttachmentPeople.Where(x => x.AttachmentId == a.Id).Select(x => x.PersonId).ToList(),
                }).ToList(),
                people = db.MemoryPeople.Where(x => x.MemoryId == m.Id).Select(x => x.PersonId).ToList(),
                comments = db.Comments.Where(c => c.MemoryId == m.Id && c.DeletedAt == null).Select(c => new { c.UserId, c.Body, c.CreatedAt }).ToList(),
                hearts = db.Reactions.Where(r => r.MemoryId == m.Id).Select(r => r.UserId).ToList(),
            }).ToListAsync(ct),
            family_tree = new
            {
                people = await db.People.AsNoTracking().Select(p => new
                {
                    p.Id, p.GivenNames, p.Surname, p.BirthSurname, p.Nickname, Sex = p.Sex.ToString(), p.BirthYear, p.BirthMonth, p.BirthDay,
                    p.BirthCirca, p.BirthPlace, p.IsLiving, p.DeathYear, p.DeathMonth, p.DeathDay, p.DeathPlace, p.Bio, p.LinkedUserId, p.PhotoAttachmentId,
                }).ToListAsync(ct),
                parents = await db.ParentChildren.AsNoTracking().Select(x => new { x.ParentId, x.ChildId, Kind = x.Kind.ToString() }).ToListAsync(ct),
                partnerships = await db.Partnerships.AsNoTracking().Select(x => new { x.Person1Id, x.Person2Id, Kind = x.Kind.ToString(), x.StartYear, x.EndYear }).ToListAsync(ct),
            },
        };

        if (File.Exists(output)) File.Delete(output);
        await using var file = File.Create(output);
        using var zip = new ZipArchive(file, ZipArchiveMode.Create);
        var json = zip.CreateEntry("data.json");
        await using (var s = json.Open())
            await JsonSerializer.SerializeAsync(s, data, new JsonSerializerOptions { WriteIndented = true }, ct);
        var ged = zip.CreateEntry("people.ged");
        await using (var w = new StreamWriter(ged.Open(), new UTF8Encoding(false)))
            await w.WriteAsync(await gedcom.ExportAsync());
        var readme = zip.CreateEntry("README.txt");
        await using (var w = new StreamWriter(readme.Open(), new UTF8Encoding(false)))
            await w.WriteAsync(ArchiveReadme);

        await progress(40, "Adding every photo, recording and document");
        if (Directory.Exists(options.MediaDir))
        {
            foreach (var path in Directory.EnumerateFiles(options.MediaDir, "*", SearchOption.AllDirectories))
            {
                ct.ThrowIfCancellationRequested();
                zip.CreateEntryFromFile(path, "media/" + Path.GetRelativePath(options.MediaDir, path).Replace('\\', '/'), CompressionLevel.NoCompression);
            }
        }
        await progress(100, "Done.");
        log.LogInformation("Portable archive written to {File}", output);
    }

    private const string ArchiveReadme = """
        FAMILY MEMORIES: PORTABLE ARCHIVE
        =================================

        This folder holds everything the family saved, in plain, open formats, so it can be read
        for decades without the app that made it.

        data.json    Every story (with all earlier versions), question, comment and heart, the people
                     who signed in, and the family tree. Plain JSON: any programmer, or any text
                     editor, can read it. Dates are in UTC.
        media/       Every photo, recording, video and document exactly as it was uploaded
                     ("original.*"), plus web copies (web.jpg, thumb.jpg, audio.m4a, poster.jpg).
                     data.json says which file belongs to which story.
        people.ged   The family tree as GEDCOM 5.5.1. Open it in any genealogy program
                     (Ancestry, FamilySearch, MyHeritage, Gramps...).

        To read the stories comfortably, also make a "Family website" export: it opens in any
        web browser, even from a USB stick.
        """;

    private static string Md(string s) =>
        new StringBuilder(s).Replace("\\", "\\\\").Replace("*", "\\*").Replace("_", "\\_").Replace("`", "\\`")
            .Replace("[", "\\[").Replace("]", "\\]").Replace("<", "\\<").Replace("#", "\\#").ToString();

    private static string Yaml(string s) => "\"" + s.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", " ") + "\"";
}
