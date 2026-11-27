using System.Net;
using System.Text.RegularExpressions;
using FamilyMemories.Data;
using FamilyMemories.Family;
using FamilyMemories.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace FamilyMemories.Tests;

/// <summary>A GEDCOM like the ones Reunion and other genealogy programs write: events with sources, shared notes, links.</summary>
internal static class ReunionSample
{
    public static string File(string joeDeath = "3 MAR 2004", bool withCensus = true) => $"""
        0 HEAD
        1 SOUR Reunion
        1 CHAR UTF-8
        0 @I1@ INDI
        1 NAME William Martin /Conlon/
        1 SEX M
        1 BIRT
        2 DATE 14 FEB 1898
        2 PLAC Cork, Ireland
        2 SOUR @S1@
        3 PAGE Entry 112
        1 IMMI
        2 DATE 1920
        2 PLAC Ellis Island, New York
        2 NOTE Arrived on the S.S. Celtic.
        2 SOUR @S2@
        3 PAGE Line 14
        1 OCCU Bricklayer
        2 PLAC Akron, Ohio
        {(withCensus ? "1 CENS\n2 DATE 1930\n2 PLAC Akron, Summit, Ohio\n2 SOUR @S3@" : "")}
        1 EVEN
        2 TYPE Union membership
        2 DATE 1935
        1 DEAT
        2 DATE {joeDeath}
        2 PLAC Akron, Ohio
        1 BURI
        2 PLAC Holy Cross Cemetery
        1 NOTE @N1@
        1 SOUR @S1@
        1 _FSFTID LZ7Q-ABC
        1 OBJE
        2 FILE https://william-martin.conlon.org/
        3 FORM URL
        2 TITL William's own page
        1 FAMS @F1@
        0 @I2@ INDI
        1 NAME Mary /Walsh/
        1 SEX F
        1 FAMS @F1@
        0 @F1@ FAM
        1 HUSB @I1@
        1 WIFE @I2@
        1 MARR
        2 DATE 12 JUN 1924
        2 PLAC St. Vincent's, Akron
        2 SOUR @S1@
        0 @N1@ NOTE Known to everyone as Bill.
        1 CONT He laid the bricks of half of North Hill.
        0 @S1@ SOUR
        1 TITL Parish register, St. Finbarr's
        0 @S2@ SOUR
        1 TITL New York passenger lists
        1 WWW https://www.familysearch.org/search/collection/1923888
        0 @S3@ SOUR
        1 TITL 1930 US Census
        0 TRLR
        """;
}

public sealed class GedcomEventTests
{
    [Fact]
    public void Reads_events_sources_shared_notes_and_links()
    {
        var data = Gedcom.Parse(ReunionSample.File());
        var bill = data.Individuals[0];
        Assert.Equal("Known to everyone as Bill.\nHe laid the bricks of half of North Hill.", bill.Note);

        var immi = bill.Facts.Single(f => f.Kind == "IMMI");
        Assert.Equal(("Immigration", "1920", "Ellis Island, New York", "Arrived on the S.S. Celtic."), (immi.Label, immi.Date.Format(), immi.Place, immi.Note));
        var passenger = Assert.Single(immi.Citations);
        Assert.Equal(new FactCitation("New York passenger lists", "Line 14", "https://www.familysearch.org/search/collection/1923888"), passenger);
        Assert.Equal(("Occupation", "Bricklayer"), (bill.Facts.Single(f => f.Kind == "OCCU").Label, bill.Facts.Single(f => f.Kind == "OCCU").Value));
        Assert.Equal("Union membership", bill.Facts.Single(f => f.Kind == "EVEN").Label);
        Assert.Equal("Parish register, St. Finbarr's", Assert.Single(bill.Facts.Single(f => f.Kind == "SOUR").Citations).Title);

        Assert.Equal(["FamilySearch", "William's own page"], bill.Links.Select(l => l.Label));
        Assert.Equal("https://www.familysearch.org/tree/person/details/LZ7Q-ABC", bill.Links[0].Url);

        var marr = Assert.Single(data.Families[0].Facts);
        Assert.Equal(("MARR", "June 12, 1924", "St. Vincent's, Akron"), (marr.Kind, marr.Date.Format(), marr.Place));
    }

    [Theory]
    [InlineData("https://www.findagrave.com/memorial/123", "Find a Grave")]
    [InlineData("https://conlon.org/familytree/public/index.html", "conlon.org")]
    public void Labels_links_by_site(string url, string label) => Assert.Equal(label, Gedcom.LinkLabel(url));
}

public sealed class MainTreeTests(PostgresFixture postgres) : IAsyncLifetime
{
    private readonly AppFactory app = new(postgres);
    public ValueTask InitializeAsync() => app.InitializeAsync();
    public ValueTask DisposeAsync() => app.DisposeAsync();

    private static async Task<string> PreviewAsync(Browser admin, string gedcom)
    {
        var page = await admin.Http.GetStringAsync("/admin/gedcom");
        var upload = new MultipartFormDataContent { { new StringContent(HtmlForms.AntiforgeryToken(page)), "__RequestVerificationToken" } };
        upload.Add(new ByteArrayContent(System.Text.Encoding.UTF8.GetBytes(gedcom)), "File", "tree.ged");
        return WebUtility.HtmlDecode(await (await admin.Http.PostAsync("/admin/gedcom?handler=Upload", upload)).Content.ReadAsStringAsync());
    }

    private static async Task CommitAsync(Browser admin, string preview, bool mainTree)
    {
        var fileId = Regex.Match(preview, "name=\"FileId\" value=\"([^\"]+)\"").Groups[1].Value;
        var fields = new List<(string, string)> { ("FileId", fileId) };
        if (mainTree) fields.Add(("MainTree", "true"));
        // Keep the suggested choice for every row ("Same person as …" when there is one).
        foreach (Match m in Regex.Matches(preview, "name=\"Decisions\\[(@[^\\]]+@)\\]\">\\s*<option value=\"([^\"]+)\""))
            fields.Add(($"Decisions[{m.Groups[1].Value}]", m.Groups[2].Value));
        var response = await admin.Http.PostFormAsync("/admin/gedcom?handler=Commit", preview, [.. fields]);
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
    }

    [Fact]
    public async Task The_main_tree_is_imported_shown_with_sources_and_updated_without_losing_the_familys_additions()
    {
        var admin = await Browser.SignedInAsync(app, AppFactory.AdminEmail);
        var first = await PreviewAsync(admin, ReunionSample.File());
        Assert.Contains("2 people and 1 families found, with 8 life events, 3 sources and 2 links", first);
        await CommitAsync(admin, first, mainTree: false);

        var bill = await app.WithDbAsync(db => db.People.SingleAsync(p => p.GivenNames == "William Martin"));
        Assert.Null(bill.Bio);   // the notes are kept with the tree's facts, not in the family's own "About"
        await app.WithServiceAsync<PeopleService, string?>(s => s.AddLinkAsync(bill.Id, "Memorial page", "https://example.org/bill"));

        var page = WebUtility.HtmlDecode(await admin.Http.GetStringAsync($"/people/{bill.Id}"));
        Assert.Contains("Life events and sources (3)", page);
        Assert.Contains("Immigration", page);
        Assert.Contains("Arrived on the S.S. Celtic.", page);
        Assert.Contains("<a href=\"https://www.familysearch.org/search/collection/1923888\" rel=\"noopener\">New York passenger lists</a>", page);
        Assert.Contains("Marriage</strong>: with Mary Walsh, St. Vincent's, Akron", page);
        Assert.Contains("Known to everyone as Bill.", page);
        Assert.Contains("William's own page", page);
        Assert.Contains("Memorial page", page);

        // A year later: the main tree corrects a date and drops the census. Imported again as the main tree.
        var again = await PreviewAsync(admin, ReunionSample.File(joeDeath: "5 MAR 2004", withCensus: false));
        Assert.Contains("Same person as William Martin Conlon (imported before)", again);
        await CommitAsync(admin, again, mainTree: true);

        var facts = await app.WithDbAsync(db => db.PersonFacts.Where(f => f.PersonId == bill.Id).ToListAsync());
        Assert.DoesNotContain(facts, f => f.Kind == "CENS");
        Assert.Equal(1, facts.Count(f => f.Kind == "IMMI"));                     // replaced, not doubled
        Assert.Equal(5, (await app.WithDbAsync(db => db.People.SingleAsync(p => p.Id == bill.Id))).DeathDay);
        var links = await app.WithDbAsync(db => db.PersonLinks.Where(l => l.PersonId == bill.Id).Select(l => l.Label).ToListAsync());
        Assert.Equal(["FamilySearch", "Memorial page", "William's own page"], links.Order());   // the hand-added link stays
        Assert.Equal(2, await app.WithDbAsync(db => db.People.CountAsync()));
    }

    [Fact]
    public async Task Export_then_import_keeps_events_sources_notes_and_links()
    {
        var admin = await Browser.SignedInAsync(app, AppFactory.AdminEmail);
        await CommitAsync(admin, await PreviewAsync(admin, ReunionSample.File()), mainTree: false);
        var exported = await admin.Http.GetStringAsync("/admin/gedcom?handler=Export");

        var original = Gedcom.Parse(ReunionSample.File()).Individuals[0];
        var back = Gedcom.Parse(exported).Individuals.Single(i => i.Given == "William Martin");
        static string Describe(GedcomFact f) => $"{f.Kind}|{f.Label}|{f.Date.Format()}|{f.Place}|{f.Value}|{f.Note}|{string.Join(";", f.Citations.Select(c => $"{c.Title}/{c.Page}/{c.Url}"))}";
        Assert.Equal(original.Facts.Select(Describe).Order(), back.Facts.Select(Describe).Order());
        Assert.Equal(original.Note, back.Note);
        Assert.Equal(original.Links.Select(l => (l.Label, l.Url)).Order(), back.Links.Select(l => (l.Label, l.Url)).Order());
        var marr = Assert.Single(Gedcom.Parse(exported).Families.SelectMany(f => f.Facts));
        Assert.Equal(("June 12, 1924", "St. Vincent's, Akron", "Parish register, St. Finbarr's"), (marr.Date.Format(), marr.Place, marr.Citations[0].Title));
    }

    [Fact]
    public async Task Links_must_be_web_addresses()
    {
        var person = await app.WithServiceAsync<PeopleService, Person>(s => s.CreateAsync(new PersonInput(
            "Ann", "Smith", null, null, Sex.Female, default, null, true, default, null, null, null, null)));
        Assert.NotNull(await app.WithServiceAsync<PeopleService, string?>(s => s.AddLinkAsync(person.Id, "x", "javascript:alert(1)")));
        Assert.Null(await app.WithServiceAsync<PeopleService, string?>(s => s.AddLinkAsync(person.Id, null, "https://www.findagrave.com/memorial/1")));
        Assert.Equal("Find a Grave", await app.WithDbAsync(db => db.PersonLinks.Select(l => l.Label).SingleAsync()));
    }
}

/// <summary>What a real Reunion 14 export taught us (made-up people; the real file is never committed).</summary>
public sealed class ReunionExportTests
{
    private const string Classic = "0 HEAD\r1 SOUR Reunion\r2 VERS V14.0\r0 @I1@ INDI\r1 NAME Anna /Byrne/\r2 SOUR @S2@\r3 PAGE obit\r1 REFN ABCD-123\r"
        + "1 BIRT\r2 DATE 4 JUL 1798\r2 PLAC Cork\r1 MILI @N1@\r1 HIST @N2@\r1 EMPL\r2 DATE 1820\r2 PLAC The mill\r1 CITN\r2 DATE 1850\r"
        + "1 RACE White\r1 SSN 000-00-0000\r1 DEAT\r2 DATE 7 SEP\r2 CAUS Fever\r1 SOUR @S2@\r0 @I2@ INDI\r1 NAME (Living)\r0 @I3@ INDI\r1 NAME (Living)\r"
        + "0 @N1@ NOTE\r1 CONT Served in the militia.\r0 @N2@ NOTE Family history.\r0 @S2@ SOUR\r1 TYPE Newspaper\r1 PERI The Cork Examiner\r1 DATE 1 Jan 1900\r1 PAGE 4\r"
        + "1 LOCA https://example.org/examiner\r0 TRLR\r";

    [Fact]
    public void Reads_a_classic_mac_file_with_reunions_own_events_and_sources()
    {
        var data = Gedcom.Parse(Classic);   // CR line endings, as Reunion writes them
        Assert.Equal(3, data.Individuals.Count);
        var anna = data.Individuals[0];
        Assert.Equal("July 4, 1798", anna.Facts.Single(f => f.Kind == "BIRT").Date.Format());             // before 1800 is fine
        Assert.Equal("Served in the militia.", anna.Facts.Single(f => f.Kind == "MILI").Note);            // a value that points at a note
        Assert.Equal(("History", "Family history."), (anna.Facts.Single(f => f.Kind == "HIST").Label, anna.Facts.Single(f => f.Kind == "HIST").Note));
        Assert.Equal(("Employment", "The mill"), (anna.Facts.Single(f => f.Kind == "EMPL").Label, anna.Facts.Single(f => f.Kind == "EMPL").Place));
        Assert.Contains(anna.Facts, f => f.Kind == "CITN" && f.Label == "Citizenship");
        Assert.DoesNotContain(anna.Facts, f => f.Kind is "RACE" or "SSN");                                // deliberately not read
        var death = anna.Facts.Single(f => f.Kind == "DEAT");
        Assert.Equal(("September 7", "Cause: Fever"), (death.Date.Format(), death.Note));                 // a day with no year, kept as words
        var source = Assert.Single(anna.Facts.Single(f => f.Kind == "SOUR").Citations.DistinctBy(c => c.Title));
        Assert.Equal(("Newspaper, The Cork Examiner, 1 Jan 1900, page 4", "https://example.org/examiner"), (source.Title, source.Url));
        Assert.Equal("https://www.familysearch.org/tree/person/details/ABCD-123", Assert.Single(anna.Links).Url);
    }

    [Theory]
    [InlineData("bet 1885  1887", "around 1885")]
    [InlineData("betw 1887 and 1888", "around 1887")]
    [InlineData("1871/1874", "around 1871")]
    [InlineData("ABT 31 MAY 1952", "around May 31, 1952")]
    [InlineData("7 SEP", "September 7")]
    [InlineData("(the summer after the war)", "the summer after the war")]
    [InlineData("1729", "1729")]
    public void Reads_the_dates_genealogists_write(string value, string expected) => Assert.Equal(expected, Gedcom.ParseDate(value).Format());

    [Fact]
    public void Living_placeholders_are_never_matched_by_name()
    {
        var existing = new[] { new Person { GivenNames = "(Living)", Surname = "" } };
        Assert.True(GedcomImporter.IsPrivacyPlaceholder("(Living)", ""));
        Assert.Null(GedcomImporter.FindDuplicate("(Living)", "", null, existing).Item1);
    }
}

public sealed class ReunionFileUploadTests(PostgresFixture postgres) : IAsyncLifetime
{
    private readonly AppFactory app = new(postgres);
    public ValueTask InitializeAsync() => app.InitializeAsync();
    public ValueTask DisposeAsync() => app.DisposeAsync();

    [Fact]
    public async Task Uploading_a_Reunion_family_file_explains_how_to_export_a_GEDCOM()
    {
        var admin = await Browser.SignedInAsync(app, AppFactory.AdminEmail);
        using var zipped = new MemoryStream();
        using (var zip = new System.IO.Compression.ZipArchive(zipped, System.IO.Compression.ZipArchiveMode.Create, leaveOpen: true))
        await using (var entry = zip.CreateEntry("Our Family.familyfile14/familyfile.familydata").Open())
            await entry.WriteAsync("3SDUAU~R...."u8.ToArray());
        foreach (var (bytes, name) in new[] { (zipped.ToArray(), "Our Family.zip"), ("3SDUAU~R...."u8.ToArray(), "familyfile.familydata") })
        {
            var page = await admin.Http.GetStringAsync("/admin/gedcom");
            var upload = new MultipartFormDataContent { { new StringContent(HtmlForms.AntiforgeryToken(page)), "__RequestVerificationToken" } };
            upload.Add(new ByteArrayContent(bytes), "File", name);
            var html = WebUtility.HtmlDecode(await (await admin.Http.PostAsync("/admin/gedcom?handler=Upload", upload)).Content.ReadAsStringAsync());
            Assert.Contains("That's a Reunion family file", html);
            Assert.Contains("File › Export › GEDCOM", html);
        }
    }
}
