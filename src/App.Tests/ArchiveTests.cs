using System.Net;
using System.Text.RegularExpressions;
using FamilyMemories.Archive;
using FamilyMemories.Data;
using FamilyMemories.Media;
using FamilyMemories.Stories;
using FamilyMemories.Tests.Infrastructure;
using ImageMagick;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace FamilyMemories.Tests;

/// <summary>A JPEG carrying the metadata a photo manager (Lightroom, Photos, a Photo CD) writes.</summary>
internal static class MetadataPhoto
{
    public static byte[] Make(string? caption = "Grandpa Bill at the brickyard", string taken = "1952:07:04 14:30:00", bool cameraJunk = false)
    {
        using var image = new MagickImage(MagickColors.SaddleBrown, 64, 48) { Format = MagickFormat.Jpeg };
        var exif = new ExifProfile();
        exif.SetValue(ExifTag.DateTimeOriginal, taken);
        if (cameraJunk) exif.SetValue(ExifTag.ImageDescription, "OLYMPUS DIGITAL CAMERA");
        image.SetProfile(exif);
        var iptc = new IptcProfile();
        if (caption is not null) iptc.SetValue(IptcTag.Caption, caption);
        iptc.SetValue(IptcTag.Keyword, "William");
        iptc.SetValue(IptcTag.Keyword, "Work");
        image.SetProfile(iptc);
        image.SetProfile(new XmpProfile(System.Text.Encoding.UTF8.GetBytes("""
            <x:xmpmeta xmlns:x="adobe:ns:meta/"><rdf:RDF xmlns:rdf="http://www.w3.org/1999/02/22-rdf-syntax-ns#">
              <rdf:Description xmlns:lr="http://ns.adobe.com/lightroom/1.0/" xmlns:dc="http://purl.org/dc/elements/1.1/">
                <lr:hierarchicalSubject><rdf:Bag><rdf:li>Family|Conlon|William</rdf:li><rdf:li>Places|Ohio|Akron</rdf:li></rdf:Bag></lr:hierarchicalSubject>
                <dc:subject><rdf:Bag><rdf:li>William</rdf:li><rdf:li>Work</rdf:li></rdf:Bag></dc:subject>
              </rdf:Description></rdf:RDF></x:xmpmeta>
            """)));
        return image.ToByteArray();
    }
}

public sealed class PhotoMetadataTests
{
    [Fact]
    public void Reads_caption_keywords_and_date_taken()
    {
        var path = Path.GetTempFileName();
        try
        {
            File.WriteAllBytes(path, MetadataPhoto.Make());
            var info = PhotoMetadata.Read(path);
            Assert.Equal("Grandpa Bill at the brickyard", info.Caption);
            Assert.Equal(new DateTime(1952, 7, 4, 14, 30, 0), info.TakenAt);
            // "William" is already the end of Family|Conlon|William, so it's not repeated; "Work" stays.
            Assert.Equal(["Family|Conlon|William", "Places|Ohio|Akron", "Work"], info.Keywords.Order());
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void A_cameras_own_label_is_not_a_caption()
    {
        var path = Path.GetTempFileName();
        try
        {
            File.WriteAllBytes(path, MetadataPhoto.Make(caption: null, cameraJunk: true));
            Assert.Null(PhotoMetadata.Read(path).Caption);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Builds_a_keyword_tree_with_counts()
    {
        var tree = ArchiveService.BuildKeywordTree([["Family|Conlon|William", "Work"], ["Family|Conlon|Mary"], ["Family|Walsh"]]);
        var family = tree.Single(n => n.Name == "Family");
        Assert.Equal(3, family.Count);
        Assert.Equal(["Conlon", "Walsh"], family.Children.Select(c => c.Name));
        Assert.Equal(2, family.Children[0].Count);
        Assert.Equal(["Mary", "William"], family.Children[0].Children.Select(c => c.Name));
        Assert.Equal(1, tree.Single(n => n.Name == "Work").Count);
    }
}

public sealed class ArchiveTests(PostgresFixture postgres) : IAsyncLifetime
{
    private readonly AppFactory app = new(postgres);
    public ValueTask InitializeAsync() => app.InitializeAsync();
    public ValueTask DisposeAsync() => app.DisposeAsync();

    private static async Task<HttpResponseMessage> SaveAsync(Browser who, string url, IEnumerable<(string Name, string Value)> fields, params (string Name, byte[] Bytes)[] files)
    {
        var page = await who.Http.GetStringAsync(url);
        var form = new MultipartFormDataContent { { new StringContent(HtmlForms.AntiforgeryToken(page)), "__RequestVerificationToken" } };
        foreach (var (n, v) in fields) form.Add(new StringContent(v), n);
        foreach (var (n, b) in files) form.Add(new ByteArrayContent(b), "Files", n);
        return await who.Http.PostAsync(url.Contains('?') ? url.Replace("?", "?handler=Save&") : url + "?handler=Save", form);
    }

    [Fact]
    public async Task A_letter_is_kept_with_its_scans_transcription_and_where_the_original_is()
    {
        var admin = await Browser.SignedInAsync(app, AppFactory.AdminEmail);
        var index = await admin.Http.GetStringAsync("/library/archive");
        await admin.Http.PostFormAsync("/library/archive?handler=NewFolder", index, ("title", "Letters"), ("description", "Letters kept by the family"));
        var folder = await app.WithDbAsync(db => db.Collections.SingleAsync());

        var saved = await SaveAsync(admin, $"/library/archive/edit?folder={folder.Id}",
            [("Title", "Letter from Joe in France"), ("When", "about 1944"), ("Place", "Normandy"), ("Folder", folder.Id.ToString()),
             ("Transcription", "Dear Rose,\nThe weather here is fine.\nLove, Joe"), ("Provenance", "Aunt Ann's cedar chest")],
            ("page1.jpg", await File.ReadAllBytesAsync(AppFactory.Fixture("photo.jpg"))), ("page2.pdf", await File.ReadAllBytesAsync(AppFactory.Fixture("recipe.pdf"))));
        Assert.Equal(HttpStatusCode.Redirect, saved.StatusCode);

        var item = await app.WithDbAsync(db => db.ArchiveItems.Include(i => i.Files).SingleAsync());
        Assert.Equal(("around 1944", "Normandy", folder.Id), (item.When.Format(), item.Place, item.CollectionId));
        Assert.Equal(2, item.Files.Count);
        Assert.All(item.Files, f => Assert.Null(f.MemoryId));
        Assert.All(item.Files, f => Assert.Equal(ProcessingState.Ready, f.State));

        // Any family member can read it and open its files.
        await app.CreateUserAsync("Lily Smith", "archive-lily@example.test", [Roles.Family]);
        var lily = await Browser.SignedInAsync(app, "archive-lily@example.test");
        var page = WebUtility.HtmlDecode(await lily.Http.GetStringAsync($"/library/archive/item/{item.Id}"));
        Assert.Contains("Letter from Joe in France", page);
        Assert.Contains("Dear Rose,\nThe weather here is fine.", page);
        Assert.Contains("Aunt Ann's cedar chest", page);
        Assert.DoesNotContain("Edit, add pages or remove", page);   // only whoever added it, and admins
        foreach (var f in item.Files)
            Assert.Equal(HttpStatusCode.OK, (await lily.Http.GetAsync($"/media/{f.Id}/original")).StatusCode);
        Assert.Contains("Letter from Joe in France", WebUtility.HtmlDecode(await lily.Http.GetStringAsync($"/library/archive/{folder.Id}")));
        Assert.NotEqual(HttpStatusCode.OK, (await lily.Http.GetAsync($"/library/archive/edit/{item.Id}")).StatusCode);
        var lilyIndex = await lily.Http.GetStringAsync("/library/archive");
        Assert.DoesNotContain("New folder", lilyIndex);
        await lily.Http.PostFormAsync("/library/archive?handler=NewFolder", lilyIndex, ("title", "Mine"));   // refused (sent to "access denied")
        Assert.Equal(1, await app.WithDbAsync(db => db.Collections.CountAsync()));
        Assert.NotEqual(HttpStatusCode.OK, (await new Browser(app).Http.GetAsync($"/media/{item.Files[0].Id}/web")).StatusCode);   // signed out: no

        // Linked to a story, it shows under "Related documents" there.
        var rose = await app.CreateUserAsync("Rose Smith", "archive-rose@example.test", [Roles.Storyteller, Roles.Family], PromptSetKind.Core);
        var story = await app.WithServiceAsync<MemoryService, Memory>(s => s.CreateFreeFormAsync(rose.Id, rose.Id));
        await app.WithServiceAsync<MemoryService, SaveResult>(s => s.SaveAsync(story.Id, new MemoryEdit("Joe's war", "He wrote every week.", default, null), rose.Id, VersionKind.Save));
        await app.WithServiceAsync<MemoryService, bool>(s => s.SetStatusAsync(story.Id, MemoryStatus.Complete, rose.Id));
        var itemPage = await admin.Http.GetStringAsync($"/library/archive/item/{item.Id}");
        await admin.Http.PostFormAsync($"/library/archive/item/{item.Id}?handler=Link", itemPage, ("memoryId", story.Id.ToString()));
        var read = WebUtility.HtmlDecode(await lily.Http.GetStringAsync($"/stories/{story.Id}"));
        Assert.Contains("Related documents", read);
        Assert.Contains($"/library/archive/item/{item.Id}", read);

        // Removing the item takes its files with it.
        var edit = await admin.Http.GetStringAsync($"/library/archive/edit/{item.Id}");
        var folders = item.Files.Select(f => app.Services.GetRequiredService<MediaStorage>().FolderFor(f)).ToList();
        await admin.Http.PostFormAsync($"/library/archive/edit/{item.Id}?handler=Delete", edit, ("confirm", "true"));
        Assert.Equal(0, await app.WithDbAsync(db => db.ArchiveItems.CountAsync()));
        Assert.Equal(0, await app.WithDbAsync(db => db.Attachments.CountAsync(a => a.ArchiveItemId != null)));
        Assert.All(folders, f => Assert.False(Directory.Exists(f)));
        Assert.Contains("Letters", WebUtility.HtmlDecode(await admin.Http.GetStringAsync("/library/archive")));   // the folder stays
    }

    [Fact]
    public async Task A_box_of_photos_becomes_one_item_each_named_dated_and_keyworded_from_the_photos()
    {
        await app.CreateUserAsync("Ann Smith", "archive-ann@example.test", [Roles.Family]);
        var ann = await Browser.SignedInAsync(app, "archive-ann@example.test");   // any family member can add
        var saved = await SaveAsync(ann, "/library/archive/edit", [("EachFileSeparately", "true")],
            ("IMG_0001.jpg", MetadataPhoto.Make()), ("IMG_0002.jpg", MetadataPhoto.Make(caption: null, taken: "1960:01:02 10:00:00")));
        Assert.Equal(HttpStatusCode.Redirect, saved.StatusCode);

        var items = await app.WithDbAsync(db => db.ArchiveItems.Include(i => i.Files).OrderBy(i => i.WhenYear).ToListAsync());
        Assert.Equal(2, items.Count);
        Assert.Equal(("Grandpa Bill at the brickyard", 1952, "Grandpa Bill at the brickyard"), (items[0].Title, items[0].WhenYear, items[0].Description));
        Assert.Equal(("IMG_0002", 1960), (items[1].Title, items[1].WhenYear));
        Assert.Contains("Family|Conlon|William", items[0].Files[0].Keywords);
        Assert.Equal(new DateTime(1952, 7, 4, 14, 30, 0, DateTimeKind.Utc), items[0].Files[0].TakenAt);

        var browse = WebUtility.HtmlDecode(await ann.Http.GetStringAsync("/library/archive"));
        Assert.Contains("Browse by keyword", browse);
        Assert.Contains("/library/archive?k=Family%7CConlon%7CWilliam", browse);
        var byKeyword = WebUtility.HtmlDecode(await ann.Http.GetStringAsync("/library/archive?k=Family%7CConlon"));
        Assert.Contains("2 items", byKeyword);
        var byPlace = WebUtility.HtmlDecode(await ann.Http.GetStringAsync("/library/archive?k=Places%7COhio%7CAkron"));
        Assert.Contains("Grandpa Bill at the brickyard", byPlace);

        // Ann can change what she added.
        Assert.Equal(HttpStatusCode.OK, (await ann.Http.GetAsync($"/library/archive/edit/{items[0].Id}")).StatusCode);
    }

    [Fact]
    public async Task A_file_belongs_to_a_story_or_an_archive_item_never_neither()
    {
        var ex = await Assert.ThrowsAnyAsync<DbUpdateException>(() => app.WithDbAsync(async db =>
        {
            db.Attachments.Add(new Attachment
            {
                Id = Guid.NewGuid(), OriginalFileName = "x.jpg", ContentType = "image/jpeg", Extension = ".jpg", Sha256 = "x",
                UploadedById = await db.Users.Select(u => u.Id).FirstAsync(), CreatedAt = DateTime.UtcNow,
            });
            await db.SaveChangesAsync();
        }));
        Assert.Contains("CK_Attachments_OneOwner", ex.InnerException!.Message);
    }

    [Fact]
    public async Task Only_an_empty_folder_can_be_removed()
    {
        var admin = await Browser.SignedInAsync(app, AppFactory.AdminEmail);
        var folder = await app.WithServiceAsync<ArchiveService, Collection>(s => s.CreateFolderAsync(null, "Certificates", null));
        await app.WithServiceAsync<ArchiveService, Collection>(s => s.CreateFolderAsync(folder.Id, "Births", null));
        Assert.NotNull(await app.WithServiceAsync<ArchiveService, string?>(s => s.DeleteFolderAsync(folder.Id)));
        Assert.Equal(2, await app.WithDbAsync(db => db.Collections.CountAsync()));
        var page = WebUtility.HtmlDecode(await admin.Http.GetStringAsync($"/library/archive/{folder.Id}"));
        Assert.Contains("Births", page);
        var root = WebUtility.HtmlDecode(await admin.Http.GetStringAsync("/library/archive"));
        Assert.Contains("action=\"/library/archive?handler=NewFolder\"", root);
        Assert.Matches(new Regex("Family archive</a>\\s*<span aria-hidden=\"true\"> › </span><a href=\"/library/archive/" + folder.Id), page);
    }
}
