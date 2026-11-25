using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using FamilyMemories.Data;
using FamilyMemories.Family;
using FamilyMemories.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace FamilyMemories.Tests;

public sealed class GedcomTests
{
    private const string Sample = """
        0 HEAD
        1 SOUR Ancestry.com
        1 GEDC
        2 VERS 5.5.1
        1 CHAR UTF-8
        0 @I1@ INDI
        1 NAME Joseph Henry /Smith/
        2 NICK Joe
        1 SEX M
        1 BIRT
        2 DATE ABT 1928
        2 PLAC Akron, Ohio, USA
        1 DEAT
        2 DATE 3 MAR 2004
        1 FAMS @F1@
        1 SOUR @S1@
        0 @I2@ INDI
        1 NAME Rose /Miller/
        1 SEX F
        1 BIRT
        2 DATE 12 JUN 1931
        1 NOTE Loved roses.
        2 CONT Grew them all her life.
        1 FAMS @F1@
        0 @I3@ INDI
        1 NAME Ann /Smith/
        1 SEX F
        1 BIRT
        2 DATE BET 1955 AND 1957
        1 FAMC @F1@
        0 @F1@ FAM
        1 HUSB @I1@
        1 WIFE @I2@
        1 CHIL @I3@
        1 MARR
        2 DATE 1952
        0 @S1@ SOUR
        1 TITL Census
        0 TRLR
        """;

    [Fact]
    public void Reads_names_dates_places_notes_and_families()
    {
        var data = Gedcom.Parse(Sample);
        Assert.Equal(3, data.Individuals.Count);
        var joe = data.Individuals[0];
        Assert.Equal(("Joseph Henry", "Smith", "Joe", Sex.Male), (joe.Given, joe.Surname, joe.Nickname, joe.Sex));
        Assert.Equal("around 1928", joe.Birth.Format());
        Assert.Equal("Akron, Ohio, USA", joe.BirthPlace);
        Assert.True(joe.Deceased);
        Assert.Equal("March 3, 2004", joe.Death.Format());
        var rose = data.Individuals[1];
        Assert.Equal("June 12, 1931", rose.Birth.Format());
        Assert.False(rose.Deceased);
        Assert.Equal("Loved roses.\nGrew them all her life.", rose.Note);
        Assert.Equal("around 1955", data.Individuals[2].Birth.Format());

        var fam = Assert.Single(data.Families);
        Assert.Equal(("@I1@", "@I2@", 1952), (fam.Husband, fam.Wife, fam.MarriageYear));
        Assert.Equal(["@I3@"], fam.Children);
    }

    [Theory]
    [InlineData("John /Smith/", "John", "Smith")]
    [InlineData("/Smith/", "", "Smith")]
    [InlineData("John Henry", "John Henry", "")]
    [InlineData("John /Smith/ Jr.", "John Jr.", "Smith")]
    public void Splits_gedcom_names(string value, string given, string surname) =>
        Assert.Equal((given, surname), Gedcom.SplitName(value));

    [Theory]
    [InlineData("12 JUN 1972", "June 12, 1972")]
    [InlineData("JUN 1972", "June 1972")]
    [InlineData("1972", "1972")]
    [InlineData("ABT 1965", "around 1965")]
    [InlineData("EST 1890", "around 1890")]
    [InlineData("BEF 1900", "around 1900")]
    [InlineData("FROM 1940 TO 1945", "around 1940")]
    [InlineData("", "")]
    [InlineData("unknown", "")]
    public void Parses_gedcom_dates(string value, string expected) => Assert.Equal(expected, Gedcom.ParseDate(value).Format());

    [Fact]
    public void Export_then_import_keeps_people_and_relationships()
    {
        var joe = new Person { Id = Guid.NewGuid(), GivenNames = "Joe", Surname = "Smith", Sex = Sex.Male, BirthYear = 1928, BirthCirca = true, IsLiving = false, DeathYear = 2004 };
        var rose = new Person { Id = Guid.NewGuid(), GivenNames = "Rose", Surname = "Smith", BirthSurname = "Miller", Sex = Sex.Female, BirthYear = 1931, BirthMonth = 6, BirthDay = 12, Bio = "Line one\nLine two" };
        var ann = new Person { Id = Guid.NewGuid(), GivenNames = "Ann", Surname = "Smith", Sex = Sex.Female, BirthYear = 1956 };
        var solo = new Person { Id = Guid.NewGuid(), GivenNames = "Tom", Surname = "Smith", Sex = Sex.Male };
        var text = Gedcom.Export([joe, rose, ann, solo],
            [new() { ParentId = joe.Id, ChildId = ann.Id }, new() { ParentId = rose.Id, ChildId = ann.Id }, new() { ParentId = rose.Id, ChildId = solo.Id }],
            [new() { Id = Guid.NewGuid(), Person1Id = joe.Id, Person2Id = rose.Id, Kind = PartnershipKind.Married, StartYear = 1952 }], "Test");

        Assert.StartsWith("0 HEAD\n", text);
        Assert.Contains("2 VERS 5.5.1", text);
        Assert.Contains("1 NAME Rose /Miller/", text); // birth name kept
        Assert.EndsWith("0 TRLR\n", text);

        var back = Gedcom.Parse(text);
        Assert.Equal(4, back.Individuals.Count);
        Assert.Equal("around 1928", back.Individuals[0].Birth.Format());
        Assert.Equal("June 12, 1931", back.Individuals[1].Birth.Format());
        Assert.Equal("Line one\nLine two", back.Individuals[1].Note);
        var couple = back.Families.Single(f => f.Husband is not null && f.Wife is not null);
        Assert.Equal(1952, couple.MarriageYear);
        Assert.Single(couple.Children);
        var single = back.Families.Single(f => f.Husband is null || f.Wife is null);
        Assert.Single(single.Children); // Tom, Rose's son from another relationship
    }

    [Fact]
    public void Duplicate_detection_matches_name_and_birth_year_or_earlier_import()
    {
        var existing = new List<Person>
        {
            new() { Id = Guid.NewGuid(), GivenNames = "Rose Ellen", Surname = "Smith", BirthSurname = "Miller", BirthYear = 1931 },
            new() { Id = Guid.NewGuid(), GivenNames = "Joe", Surname = "Smith", GedcomId = "@I1@" },
        };
        GedcomIndividual Ind(string xref, string given, string surname, int? year) =>
            new(xref, given, surname, null, Sex.Unknown, new FuzzyDate(year, null, null, false, null, null), null, false, default, null, null);

        Assert.Equal(existing[0], GedcomImporter.FindDuplicate(Ind("@X@", "Rose", "Miller", 1931), existing).Item1);   // maiden name
        Assert.Equal(existing[0], GedcomImporter.FindDuplicate(Ind("@X@", "Rose", "Smith", null), existing).Item1);    // no year to disagree
        Assert.Null(GedcomImporter.FindDuplicate(Ind("@X@", "Rose", "Smith", 1960), existing).Item1);                  // different generation
        Assert.Equal(existing[1], GedcomImporter.FindDuplicate(Ind("@I1@", "Joseph", "Smith", null), existing).Item1); // same GEDCOM id
    }
}

public sealed class FamilyTreeTests(PostgresFixture postgres) : IAsyncLifetime
{
    private readonly AppFactory app = new(postgres);
    public ValueTask InitializeAsync() => app.InitializeAsync();
    public ValueTask DisposeAsync() => app.DisposeAsync();

    private static string Text(string html) => WebUtility.HtmlDecode(Regex.Replace(html, "<[^>]+>", ""));

    [Fact]
    public async Task Gedcom_import_previews_duplicates_then_builds_the_tree()
    {
        var existingRose = await app.WithServiceAsync<PeopleService, Person>(s => s.CreateAsync(new PersonInput(
            "Rose", "Smith", "Miller", null, Sex.Female, new FuzzyDate(1931, null, null, false, null, null), null, true, default, null, "Typed by the family", null, null)));
        var admin = await Browser.SignedInAsync(app, AppFactory.AdminEmail);
        var page = await admin.Http.GetStringAsync("/admin/gedcom");
        var gedcom = """
            0 HEAD
            0 @I1@ INDI
            1 NAME Joseph /Smith/
            1 SEX M
            1 BIRT
            2 DATE 1928
            1 DEAT Y
            0 @I2@ INDI
            1 NAME Rose /Miller/
            1 SEX F
            1 BIRT
            2 DATE 12 JUN 1931
            2 PLAC Akron
            0 @I3@ INDI
            1 NAME Ann /Smith/
            0 @F1@ FAM
            1 HUSB @I1@
            1 WIFE @I2@
            1 CHIL @I3@
            0 TRLR
            """;
        var upload = new MultipartFormDataContent { { new StringContent(HtmlForms.AntiforgeryToken(page)), "__RequestVerificationToken" } };
        upload.Add(new ByteArrayContent(System.Text.Encoding.UTF8.GetBytes(gedcom)), "File", "family.ged");
        var preview = WebUtility.HtmlDecode(await (await admin.Http.PostAsync("/admin/gedcom?handler=Upload", upload)).Content.ReadAsStringAsync());
        Assert.Contains("3 people and 1 families found", preview);
        Assert.Contains("Same person as Rose Smith", preview);

        var fileId = Regex.Match(preview, "name=\"FileId\" value=\"([^\"]+)\"").Groups[1].Value;
        await admin.Http.PostFormAsync("/admin/gedcom?handler=Commit", preview, ("FileId", fileId),
            ("Decisions[@I1@]", "new"), ("Decisions[@I2@]", existingRose.Id.ToString()), ("Decisions[@I3@]", "new"));

        var people = await app.WithDbAsync(db => db.People.ToListAsync());
        Assert.Equal(3, people.Count);
        var rose = people.Single(p => p.Id == existingRose.Id);
        Assert.Equal("Typed by the family", rose.Bio);  // kept
        Assert.Equal("Akron", rose.BirthPlace);          // gap filled in
        var joe = people.Single(p => p.GivenNames == "Joseph");
        Assert.False(joe.IsLiving);
        var kin = await app.WithServiceAsync<PeopleService, Kin?>(s => s.KinAsync(people.Single(p => p.GivenNames == "Ann").Id));
        Assert.Equal(["Joseph", "Rose"], kin!.Parents.Select(p => p.GivenNames).Order());
        Assert.Single((await app.WithServiceAsync<PeopleService, Kin?>(s => s.KinAsync(joe.Id)))!.Partners);

        // Export includes everyone.
        var export = await admin.Http.GetStringAsync("/admin/gedcom?handler=Export");
        Assert.Contains("1 NAME Joseph /Smith/", export);
        Assert.Contains("0 @F1@ FAM", export);
    }

    [Fact]
    public async Task People_pages_show_family_stories_and_photos_with_birth_dates_hidden_for_living_people()
    {
        var rose = await app.CreateUserAsync("Rose Smith", "tree-rose@example.test", [Roles.Storyteller, Roles.Family], PromptSetKind.Core);
        var lily = await app.CreateUserAsync("Lily", "tree-lily@example.test", [Roles.Family]);
        var roseBrowser = await Browser.SignedInAsync(app, "tree-rose@example.test");
        var lilyBrowser = await Browser.SignedInAsync(app, "tree-lily@example.test");

        // Lily adds Rose (living) and Grandpa Joe (died) through the pages, and connects them.
        var newPage = await lilyBrowser.Http.GetStringAsync("/people/new/edit");
        var added = await lilyBrowser.Http.PostFormAsync("/people/new/edit?handler=Save", newPage,
            ("GivenNames", "Rose"), ("Surname", "Smith"), ("BirthYear", "1931"), ("BirthMonth", "6"), ("BirthDay", "12"), ("IsLiving", "true"), ("Sex", "Female"));
        var roseId = Guid.Parse(added.Headers.Location!.OriginalString.Split('/')[2]);
        var joe = await app.WithServiceAsync<PeopleService, Person>(s => s.CreateAsync(new PersonInput(
            "Joe", "Smith", null, "Grandpa Joe", Sex.Male, new FuzzyDate(1928, 3, 1, false, null, null), null, false, new FuzzyDate(2004, null, null, false, null, null), null, null, null, null)));
        var edit = await lilyBrowser.Http.GetStringAsync($"/people/{roseId}/edit");
        await lilyBrowser.Http.PostFormAsync($"/people/{roseId}/edit?handler=Relate", edit, ("Relation", "spouse"), ("OtherId", joe.Id.ToString()));
        await app.WithDbAsync(db => db.People.Where(p => p.Id == roseId).ExecuteUpdateAsync(s => s.SetProperty(p => p.LinkedUserId, rose.Id)));

        // Lily (not an admin) sees only the year for living Rose; full dates for Joe, who has died.
        var rosePage = Text(await lilyBrowser.Http.GetStringAsync($"/people/{roseId}"));
        Assert.Contains("Born 1931", rosePage);
        Assert.DoesNotContain("June 12", rosePage);
        Assert.Contains("Married to", rosePage);
        Assert.Contains("Joe Smith", rosePage);
        var admin = await Browser.SignedInAsync(app, AppFactory.AdminEmail);
        Assert.Contains("Born June 12, 1931", Text(await admin.Http.GetStringAsync($"/people/{roseId}")));
        Assert.Contains("March 1, 1928 – 2004", Text(await lilyBrowser.Http.GetStringAsync($"/people/{joe.Id}")));

        // Rose writes a story about Joe and tags him in a photo.
        var joePage = await roseBrowser.Http.GetStringAsync($"/people/{joe.Id}");
        var writeAbout = await roseBrowser.Http.PostFormAsync("/stories/new", joePage, ("about", joe.Id.ToString()));
        var memoryId = Guid.Parse(writeAbout.Headers.Location!.OriginalString.Split('/')[2]);
        var (_, writePage, _) = await roseBrowser.GetAsync($"/stories/{memoryId}/write");
        Assert.Contains("A story about Joe Smith", WebUtility.HtmlDecode(writePage));
        var form = new MultipartFormDataContent
        {
            { new StringContent(HtmlForms.AntiforgeryToken(writePage)), "__RequestVerificationToken" },
            { new StringContent("He fixed every tractor in the county."), "Body" },
            { new StringContent("About Grandpa Joe"), "Title" },
            { new StringContent("true"), "PeopleFormPresent" },
            { new StringContent(joe.Id.ToString()), "PeopleIds" },
            { new StringContent("Rose Smith"), "AddPerson" },
            { new StringContent("AllFamily"), "Visibility" },
        };
        form.Add(new ByteArrayContent(await File.ReadAllBytesAsync(AppFactory.Fixture("photo.jpg"))), "Files", "tractor.jpg");
        await roseBrowser.Http.PostAsync($"/stories/{memoryId}/write?handler=Save", form);
        var attachmentId = await app.WithDbAsync(db => db.Attachments.Where(a => a.MemoryId == memoryId).Select(a => a.Id).SingleAsync());
        (_, writePage, _) = await roseBrowser.GetAsync($"/stories/{memoryId}/write");
        var tag = new MultipartFormDataContent
        {
            { new StringContent(HtmlForms.AntiforgeryToken(writePage)), "__RequestVerificationToken" },
            { new StringContent("He fixed every tractor in the county."), "Body" },
            { new StringContent("About Grandpa Joe"), "Title" },
            { new StringContent(attachmentId.ToString()), "PhotoFormPresent" },
            { new StringContent("Grandpa Joe"), $"PhotoAddPerson[{attachmentId}]" },
        };
        await roseBrowser.Http.PostAsync($"/stories/{memoryId}/write?handler=Finish", tag);

        Assert.Equal(2, await app.WithDbAsync(db => db.MemoryPeople.CountAsync(x => x.MemoryId == memoryId)));
        joePage = Text(await lilyBrowser.Http.GetStringAsync($"/people/{joe.Id}"));
        Assert.Contains("Stories about Joe", joePage);
        Assert.Contains("About Grandpa Joe", joePage);
        Assert.Contains("Photos Joe appears in", joePage);
        Assert.Contains("Stories told by Rose", Text(await lilyBrowser.Http.GetStringAsync($"/people/{roseId}")));
        Assert.Contains("In this photo: Joe Smith", Text(await lilyBrowser.Http.GetStringAsync($"/stories/{memoryId}")));

        // The tree page and its data.
        var tree = await lilyBrowser.Http.GetAsync($"/tree?focus={roseId}");
        Assert.Contains("style-src-attr 'unsafe-inline'", tree.Headers.GetValues("Content-Security-Policy").Single());
        Assert.DoesNotContain("unsafe-inline';", tree.Headers.GetValues("Content-Security-Policy").Single().Replace("style-src-attr 'unsafe-inline'", ""));
        var json = await lilyBrowser.Http.GetFromJsonAsync<JsonElement>("/api/tree");
        var roseNode = json.EnumerateArray().Single(n => n.GetProperty("id").GetString() == roseId.ToString());
        Assert.Equal("b. 1931", roseNode.GetProperty("data").GetProperty("years").GetString());
        Assert.Equal(joe.Id.ToString(), roseNode.GetProperty("rels").GetProperty("spouses")[0].GetString());
    }

    [Fact]
    public async Task Nobody_can_become_their_own_ancestor()
    {
        var people = new List<Person>();
        foreach (var name in new[] { "Grandma", "Mom", "Kid" })
            people.Add(await app.WithServiceAsync<PeopleService, Person>(s => s.CreateAsync(new PersonInput(name, "X", null, null, Sex.Unknown, default, null, true, default, null, null, null, null))));
        Assert.Null(await app.WithServiceAsync<PeopleService, string?>(s => s.AddParentAsync(people[1].Id, people[0].Id, ParentKind.Biological)));
        Assert.Null(await app.WithServiceAsync<PeopleService, string?>(s => s.AddParentAsync(people[2].Id, people[1].Id, ParentKind.Biological)));
        Assert.NotNull(await app.WithServiceAsync<PeopleService, string?>(s => s.AddParentAsync(people[0].Id, people[2].Id, ParentKind.Biological)));
        Assert.NotNull(await app.WithServiceAsync<PeopleService, string?>(s => s.AddParentAsync(people[0].Id, people[0].Id, ParentKind.Biological)));
    }

    [Fact]
    public async Task Only_admins_can_remove_people_and_link_accounts()
    {
        var rose = await app.CreateUserAsync("Rose", "tree-perm@example.test", [Roles.Family]);
        var person = await app.WithServiceAsync<PeopleService, Person>(s => s.CreateAsync(new PersonInput("Joe", "X", null, null, Sex.Male, default, null, true, default, null, null, null, null)));
        var browser = await Browser.SignedInAsync(app, "tree-perm@example.test");
        var edit = await browser.Http.GetStringAsync($"/people/{person.Id}/edit");
        Assert.DoesNotContain("Remove from the tree", edit);
        Assert.DoesNotContain("Signs in as", edit);
        await browser.Http.PostFormAsync($"/people/{person.Id}/edit?handler=Save", edit, ("GivenNames", "Joe"), ("Surname", "X"), ("IsLiving", "true"), ("LinkedUserId", rose.Id.ToString()));
        Assert.Null(await app.WithDbAsync(db => db.People.Where(p => p.Id == person.Id).Select(p => p.LinkedUserId).SingleAsync()));
        var delete = await browser.Http.PostFormAsync($"/people/{person.Id}/edit?handler=Delete", edit);
        Assert.Contains("/account/access-denied", delete.Headers.Location!.ToString());
        Assert.True(await app.WithDbAsync(db => db.People.AnyAsync(p => p.Id == person.Id)));
    }
}
