using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using FamilyMemories.Data;
using FamilyMemories.Family;
using FamilyMemories.Imports;
using FamilyMemories.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace FamilyMemories.Tests;

public sealed class CsvTests
{
    [Fact]
    public void Reads_quotes_line_breaks_bom_and_loose_headers()
    {
        var text = "﻿First Name,last_name,Story,Shoe size\r\n"
                 + "Rose,Smith,\"He said, \"\"Then you'd better\r\nbuy me a lemonade.\"\"\",7\r\n"
                 + ",,,\r\n"
                 + "Joe,Smith,,9";
        var (rows, unknown) = Csv.Read(text, new Dictionary<string, string> { ["firstname"] = "given", ["lastname"] = "surname", ["story"] = "body" });
        Assert.Equal(["Shoe size"], unknown);
        Assert.Equal(2, rows.Count);
        Assert.Equal(("Rose", "Smith"), (rows[0].Get("given"), rows[0].Get("surname")));
        Assert.Equal("He said, \"Then you'd better\nbuy me a lemonade.\"", rows[0].Get("body"));
        Assert.Equal(2, rows[0].Line);
        Assert.Equal(5, rows[1].Line);   // counts the line break inside the quoted cell, and the blank row
        Assert.Null(rows[1].Get("body"));
    }

    [Fact]
    public void Reads_semicolon_separated_files_and_joins_repeated_columns()
    {
        var (rows, _) = Csv.Read("Name;Mother;Father\nAnn Smith;Rose Smith;Joe Smith\n",
            PeopleCsvImporter.Columns);
        Assert.Equal("Ann Smith", rows[0].Get("name"));
        Assert.Equal(["Rose Smith", "Joe Smith"], Csv.Names(rows[0].Get("parents")));
    }

    [Fact]
    public void Templates_read_back_with_every_column_recognised()
    {
        var (people, unknownPeople) = Csv.Read(PeopleCsvImporter.Template(), PeopleCsvImporter.Columns);
        Assert.Empty(unknownPeople);
        Assert.Equal(3, people.Count);
        var (stories, unknownStories) = Csv.Read(StoriesCsvImporter.Template(), StoriesCsvImporter.Columns);
        Assert.Empty(unknownStories);
        Assert.Contains('\n', stories[0].Get("body")!);
    }

    [Theory]
    [InlineData("1965", "1965", true)]
    [InlineData("about 1965", "around 1965", true)]
    [InlineData("c. 1965", "around 1965", true)]
    [InlineData("~1965", "around 1965", true)]
    [InlineData("1970s", "1970s", true)]
    [InlineData("the 1940s", "1940s", true)]
    [InlineData("June 1972", "June 1972", true)]
    [InlineData("June 3, 1972", "June 3, 1972", true)]
    [InlineData("3 June 1972", "June 3, 1972", true)]
    [InlineData("1972-06-03", "June 3, 1972", true)]
    [InlineData("6/3/1972", "June 3, 1972", true)]
    [InlineData("25/12/1950", "December 25, 1950", true)]
    [InlineData("26472", "June 22, 1972", true)]   // an Excel date number
    [InlineData("", "", true)]
    [InlineData("the summer after the war", "the summer after the war", false)]
    [InlineData("1492", "1492", false)]
    public void Reads_dates_the_way_people_type_them(string text, string expected, bool understood)
    {
        var (date, ok) = DateText.Parse(text, 2026);
        Assert.Equal(expected, date.Format());
        Assert.Equal(understood, ok);
    }

    [Theory]
    [InlineData("I worked at the five-and-dime. It paid 35 cents.", "I worked at the five-and-dime")]
    [InlineData("We drove all the way to California in a 1949 Hudson with four children and a dog in the back seat", "We drove all the way to California in a 1949 Hudson with…")]
    public void Makes_a_title_from_the_first_sentence(string body, string expected) =>
        Assert.Equal(expected, StoriesCsvImporter.TitleFromBody(body));
}

public sealed class ImportTests(PostgresFixture postgres) : IAsyncLifetime
{
    private readonly AppFactory app = new(postgres);
    public ValueTask InitializeAsync() => app.InitializeAsync();
    public ValueTask DisposeAsync() => app.DisposeAsync();

    private static async Task<string> UploadAsync(Browser admin, string page, string csv)
    {
        var form = await admin.Http.GetStringAsync(page);
        var upload = new MultipartFormDataContent { { new StringContent(HtmlForms.AntiforgeryToken(form)), "__RequestVerificationToken" } };
        upload.Add(new ByteArrayContent(Encoding.UTF8.GetBytes(csv)), "File", "sheet.csv");
        var response = await admin.Http.PostAsync(page + "?handler=Upload", upload);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return WebUtility.HtmlDecode(await response.Content.ReadAsStringAsync());
    }

    private static string FileId(string html) => Regex.Match(html, "name=\"FileId\" value=\"([^\"]+)\"").Groups[1].Value;

    [Fact]
    public async Task People_spreadsheet_builds_the_tree_and_makes_accounts()
    {
        var existingRose = await app.WithServiceAsync<PeopleService, Person>(s => s.CreateAsync(new PersonInput(
            "Rose", "Smith", null, null, Sex.Female, new FuzzyDate(1931, null, null, false, null, null), null, true, default, null, "Typed by the family", null, null)));
        var admin = await Browser.SignedInAsync(app, AppFactory.AdminEmail);
        var before = app.Email.Sent.Count;

        var csv = """
            First name,Last name,Maiden name,Born,Died,Parents,Partner,Email,Role,Favourite colour
            Rose,Smith,Miller,"June 12, 1931",,,Joseph Smith,,,red
            Joseph,Smith,,about 1928,2004,,Rose Smith,,,
            Ann,Smith,,1956,,Joseph Smith; Rose Smith,,ann@example.test,storyteller,
            ,,,,,,,,,
            Lily,Jones,,1990,,Ann Smith; Bob Nobody,,lily@example.test,,
            ,,,1950,,,,,,
            """;
        var preview = await UploadAsync(admin, "/admin/import/people", csv);
        Assert.Contains("5 people found", preview);
        Assert.Contains("Same person as Rose Smith", preview);
        Assert.Contains("These columns will be ignored: Favourite colour", preview);
        Assert.Contains("New account for ann@example.test (Storyteller, Family)", preview);
        Assert.Contains("No name, so this row can't be imported.", preview);

        var page = await admin.Http.PostFormAsync("/admin/import/people?handler=Commit", preview,
            ("FileId", FileId(preview)), ("Decisions[2]", existingRose.Id.ToString()), ("SendInvitations", "true"));
        var done = WebUtility.HtmlDecode(await page.Content.ReadAsStringAsync());
        Assert.Contains("3 new people added", done);
        Assert.Contains("1 matched", done);
        Assert.Contains("couldn't tell who \"Bob Nobody\" is", done);

        var people = await app.WithDbAsync(db => db.People.ToListAsync());
        Assert.Equal(4, people.Count);
        var rose = people.Single(p => p.Id == existingRose.Id);
        Assert.Equal(("Typed by the family", "Miller"), (rose.Bio, rose.BirthSurname));   // kept, and the blank filled in
        var joe = people.Single(p => p.GivenNames == "Joseph");
        Assert.False(joe.IsLiving);
        Assert.Equal("around 1928", joe.Birth.Format());
        var ann = people.Single(p => p.GivenNames == "Ann");
        var kin = await app.WithServiceAsync<PeopleService, Kin?>(s => s.KinAsync(ann.Id));
        Assert.Equal(["Joseph", "Rose"], kin!.Parents.Select(p => p.GivenNames).Order());
        Assert.Single((await app.WithServiceAsync<PeopleService, Kin?>(s => s.KinAsync(joe.Id)))!.Partners);   // named on both rows, recorded once
        Assert.Equal(["Ann"], (await app.WithServiceAsync<PeopleService, Kin?>(s => s.KinAsync(people.Single(p => p.GivenNames == "Lily").Id)))!.Parents.Select(p => p.GivenNames));

        var annUser = await app.WithDbAsync(db => db.Users.Include(u => u.PromptSet).SingleAsync(u => u.Email == "ann@example.test"));
        Assert.Equal(annUser.Id, ann.LinkedUserId);
        Assert.Equal(PromptSetKind.Core, annUser.PromptSet!.Kind);
        Assert.NotNull(await app.WithDbAsync(db => db.Users.SingleOrDefaultAsync(u => u.Email == "lily@example.test")));
        Assert.Equal(2, app.Email.Sent.Count - before);   // an invitation each for Ann and Lily

        // Running the same sheet again offers every named person as "same person".
        var again = await UploadAsync(admin, "/admin/import/people", csv);
        Assert.Contains("Same person as Ann Smith", again);
        Assert.Contains("Linked to Ann Smith's account", again);
    }

    [Fact]
    public async Task Stories_spreadsheet_adds_answers_and_stories_of_their_own()
    {
        var rose = await app.CreateUserAsync("Rose Smith", "rose@example.test", [Roles.Storyteller, Roles.Family], PromptSetKind.Core);
        var lily = await app.CreateUserAsync("Lily Smith", "lily@example.test", [Roles.Family]);
        var joe = await app.WithServiceAsync<PeopleService, Person>(s => s.CreateAsync(new PersonInput(
            "Joseph", "Smith", null, "Joe", Sex.Male, default, null, false, default, null, null, null, null)));
        var prompt = await app.WithDbAsync(db => db.Prompts.OrderBy(p => p.SortOrder).FirstAsync(p => p.IsCore));
        var admin = await Browser.SignedInAsync(app, AppFactory.AdminEmail);

        var csv = $"""
            Storyteller,Title,Story,Question,When,Place,People,Written by,Status,Who can see
            Rose Smith,,"I was born at home.
            In the front bedroom.","{prompt.Question}",1931,Akron,,,,
            rose@example.test,The county fair,Joe pinned the ribbon on my coat.,,summer after the war,Summit County Fair,Joe; Nobody,Lily Smith,,Only me
            Rose Smith,The county fair,The same story twice.,,,,,,,
            Grandpa Who,A story,Words.,,,,,,,
            Rose Smith,Unfinished,Still writing this one.,,,,,,Draft,
            """;
        var preview = await UploadAsync(admin, "/admin/import/stories", csv);
        Assert.Contains("5 stories found; 3 ready to add", preview);
        Assert.Contains($"Answers \"{prompt.Question}\"", preview);
        Assert.Contains("No one called \"Grandpa Who\" signs in here", preview);
        Assert.Contains("The same story is on an earlier row.", preview);
        Assert.Contains("is kept as words", preview);

        var page = await admin.Http.PostFormAsync("/admin/import/stories?handler=Commit", preview, ("FileId", FileId(preview)));
        var done = WebUtility.HtmlDecode(await page.Content.ReadAsStringAsync());
        Assert.Contains("3 stories added", done);
        Assert.Contains("couldn't find \"Nobody\"", done);

        var stories = await app.WithDbAsync(db => db.Memories.Where(m => m.StorytellerId == rose.Id && m.Body != "").ToListAsync());
        Assert.Equal(3, stories.Count);
        var answer = stories.Single(m => m.PromptId == prompt.Id);
        Assert.Equal((prompt.Title, "I was born at home.\nIn the front bedroom.", MemoryStatus.Complete, (Guid?)null),
            (answer.Title, answer.Body, answer.Status, answer.WrittenById));
        Assert.Equal(1931, answer.WhenYear);
        var fair = stories.Single(m => m.Title == "The county fair");
        Assert.Equal((lily.Id, Visibility.Private, "summer after the war"), (fair.WrittenById, fair.Visibility, fair.WhenLabel));
        Assert.Equal([joe.Id], await app.WithDbAsync(db => db.MemoryPeople.Where(x => x.MemoryId == fair.Id).Select(x => x.PersonId).ToListAsync()));
        Assert.Equal(MemoryStatus.Draft, stories.Single(m => m.Title == "Unfinished").Status);

        // The same sheet again: everything is already here, so every row defaults to Skip.
        var again = await UploadAsync(admin, "/admin/import/stories", csv);
        Assert.Contains("0 ready to add", again);
    }

    [Fact]
    public async Task Import_pages_are_for_admins_only_and_offer_templates()
    {
        await app.CreateUserAsync("Lily Smith", "lily@example.test", [Roles.Family]);
        var lily = await Browser.SignedInAsync(app, "lily@example.test");
        Assert.NotEqual(HttpStatusCode.OK, (await lily.Http.GetAsync("/admin/import/people")).StatusCode);
        Assert.NotEqual(HttpStatusCode.OK, (await lily.Http.GetAsync("/admin/import/stories")).StatusCode);

        var admin = await Browser.SignedInAsync(app, AppFactory.AdminEmail);
        var template = await admin.Http.GetAsync("/admin/import/stories?handler=Template");
        Assert.Equal("text/csv", template.Content.Headers.ContentType?.MediaType);
        Assert.StartsWith("Storyteller,Title,Story", (await template.Content.ReadAsStringAsync()).TrimStart('﻿'));
    }
}
