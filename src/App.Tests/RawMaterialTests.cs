using System.Net;
using FamilyMemories.Data;
using FamilyMemories.Stories;
using FamilyMemories.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace FamilyMemories.Tests;

public sealed class RawMaterialTests(PostgresFixture postgres) : IAsyncLifetime
{
    private readonly AppFactory app = new(postgres);
    public ValueTask InitializeAsync() => app.InitializeAsync();
    public ValueTask DisposeAsync() => app.DisposeAsync();

    [Fact]
    public async Task A_helper_keeps_a_letter_transcription_copies_it_in_and_readers_never_see_it()
    {
        var rose = await app.CreateUserAsync("Rose Smith", "raw-rose@example.test", [Roles.Storyteller, Roles.Family], PromptSetKind.Core);
        await app.CreateUserAsync("Lily Smith", "raw-lily@example.test", [Roles.Family]);
        var memory = await app.WithServiceAsync<MemoryService, Memory>(s => s.CreateFreeFormAsync(rose.Id, rose.Id));
        await app.WithServiceAsync<MemoryService, SaveResult>(s => s.SaveAsync(memory.Id, new MemoryEdit("Letters from Joe", "Joe wrote every week.", default, null), rose.Id, VersionKind.Save));

        var admin = await Browser.SignedInAsync(app, AppFactory.AdminEmail);   // writing for Rose: the raw-material tools show
        var page = WebUtility.HtmlDecode(await admin.Http.GetStringAsync($"/stories/{memory.Id}/write"));
        Assert.Contains("Raw material", page);
        await admin.Http.PostFormAsync($"/stories/{memory.Id}/write?handler=AddRawMaterial", page,
            ("Body", "Joe wrote every week."), ("RawText", "Dear Rose, the weather here is fine. Love, Joe"), ("RawKind", "PaperScan"));

        var source = await app.WithDbAsync(db => db.MemorySources.SingleAsync(x => x.MemoryId == memory.Id));
        Assert.Equal(SourceKind.PaperScan, source.Kind);
        Assert.Equal("Joe wrote every week.", await app.WithDbAsync(db => db.Memories.Where(m => m.Id == memory.Id).Select(m => m.Body).SingleAsync()));

        page = WebUtility.HtmlDecode(await admin.Http.GetStringAsync($"/stories/{memory.Id}/write"));
        Assert.Contains("Transcription of a letter or paper page", page);
        Assert.Contains("Dear Rose, the weather here is fine.", page);
        await admin.Http.PostFormAsync($"/stories/{memory.Id}/write?handler=CopyRawMaterial", page, ("Body", "Joe wrote every week."), ("sourceId", source.Id.ToString()));
        Assert.Equal("Joe wrote every week.\n\nDear Rose, the weather here is fine. Love, Joe",
            await app.WithDbAsync(db => db.Memories.Where(m => m.Id == memory.Id).Select(m => m.Body).SingleAsync()));
        Assert.Equal(1, await app.WithDbAsync(db => db.MemorySources.CountAsync()));   // the raw material itself is unchanged

        await app.WithServiceAsync<MemoryService, bool>(s => s.SetStatusAsync(memory.Id, MemoryStatus.Complete, rose.Id));
        var lily = await Browser.SignedInAsync(app, "raw-lily@example.test");
        var read = WebUtility.HtmlDecode(await lily.Http.GetStringAsync($"/stories/{memory.Id}"));
        Assert.DoesNotContain("Raw material", read);
        Assert.NotEqual(HttpStatusCode.OK, (await lily.Http.GetAsync($"/stories/{memory.Id}/write")).StatusCode);
    }

    [Fact]
    public async Task Spreadsheet_imports_keep_the_original_text_as_raw_material()
    {
        await app.CreateUserAsync("Rose Smith", "raw-import@example.test", [Roles.Storyteller], PromptSetKind.Core);
        var admin = await Browser.SignedInAsync(app, AppFactory.AdminEmail);
        var form = await admin.Http.GetStringAsync("/admin/import/stories");
        var upload = new MultipartFormDataContent { { new StringContent(HtmlForms.AntiforgeryToken(form)), "__RequestVerificationToken" } };
        upload.Add(new ByteArrayContent(System.Text.Encoding.UTF8.GetBytes("Storyteller,Title,Story\nRose Smith,My first job,I worked at the five-and-dime.\n")), "File", "s.csv");
        var preview = WebUtility.HtmlDecode(await (await admin.Http.PostAsync("/admin/import/stories?handler=Upload", upload)).Content.ReadAsStringAsync());
        var fileId = System.Text.RegularExpressions.Regex.Match(preview, "name=\"FileId\" value=\"([^\"]+)\"").Groups[1].Value;
        await admin.Http.PostFormAsync("/admin/import/stories?handler=Commit", preview, ("FileId", fileId));

        var raw = await app.WithDbAsync(db => db.MemorySources.SingleAsync());
        Assert.Equal((SourceKind.Import, "I worked at the five-and-dime."), (raw.Kind, raw.Text));
    }
}
