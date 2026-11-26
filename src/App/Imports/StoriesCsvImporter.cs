using FamilyMemories.Admin;
using FamilyMemories.Configuration;
using FamilyMemories.Data;
using FamilyMemories.Family;
using FamilyMemories.Stories;
using Microsoft.EntityFrameworkCore;

namespace FamilyMemories.Imports;

public sealed record StoryLine(
    int Line, string StorytellerText, string? Title, string Body, string? Question, FuzzyDate When, string? Place,
    IReadOnlyList<string> People, string? WrittenByText, bool Finished, Visibility? Visibility);

/// <param name="Storyteller">The account the story will belong to, when we could tell who it is.</param>
/// <param name="Prompt">The question it answers, when the "Question" column matched one.</param>
/// <param name="Duplicate">Something already in the library that looks the same; such rows default to Skip.</param>
public sealed record StoryImportRow(
    StoryLine Story, AppUser? Storyteller, AppUser? WrittenBy, Prompt? Prompt, string? Duplicate,
    IReadOnlyList<string> Notes, string? Problem)
{
    public string Title => Story.Title ?? Prompt?.Title ?? StoriesCsvImporter.TitleFromBody(Story.Body);
}

public sealed record StoryImportPreview(Guid FileId, IReadOnlyList<StoryImportRow> Rows, IReadOnlyList<string> UnknownColumns);

public sealed record StoryImportResult(int Imported, int Skipped, int PeopleTagged, IReadOnlyList<string> Warnings);

/// <summary>
/// Adds stories from a spreadsheet: one row per story, with who told it, the words, and optionally the question it answers,
/// when it happened, where, and who's in it. Upload, check a preview, then import.
/// </summary>
public sealed class StoriesCsvImporter(
    AppDbContext db, MemoryService memories, PeopleService people, AuditLog audit, AppOptions options, TimeProvider clock)
{
    public const long MaxFileBytes = 20 * 1024 * 1024;
    public const int MaxRows = 5000;

    public static readonly IReadOnlyDictionary<string, string> Columns = new Dictionary<string, string>
    {
        ["storyteller"] = "storyteller", ["toldby"] = "storyteller", ["teller"] = "storyteller", ["who"] = "storyteller",
        ["author"] = "storyteller", ["name"] = "storyteller", ["by"] = "storyteller", ["person"] = "storyteller",
        ["storytelleremail"] = "storyteller",
        ["title"] = "title", ["storytitle"] = "title", ["heading"] = "title", ["subject"] = "title",
        ["story"] = "body", ["body"] = "body", ["text"] = "body", ["memory"] = "body", ["answer"] = "body", ["content"] = "body",
        ["words"] = "body", ["storytext"] = "body",
        ["question"] = "question", ["prompt"] = "question",
        ["when"] = "when", ["date"] = "when", ["year"] = "when", ["whenithappened"] = "when",
        ["place"] = "place", ["where"] = "place", ["location"] = "place",
        ["people"] = "people", ["peopleinthisstory"] = "people", ["whosinit"] = "people", ["tagged"] = "people", ["peopleinit"] = "people",
        ["writtenby"] = "writtenby", ["typedby"] = "writtenby", ["helper"] = "writtenby", ["writtendownby"] = "writtenby",
        ["status"] = "status", ["finished"] = "status",
        ["visibility"] = "visibility", ["whocansee"] = "visibility", ["sharedwith"] = "visibility", ["sharing"] = "visibility",
    };

    public static string Template() =>
        Csv.Line("Storyteller", "Title", "Story", "Question", "When", "Place", "People", "Written by", "Status", "Who can see")
        + Csv.Line("Rose Smith", "The county fair", "Joe had entered his tomatoes, and he won the blue ribbon.\nHe walked right over and pinned it on my coat.",
            "How did you meet?", "1950", "Summit County Fair", "Joseph Smith; Rose Smith", "", "Finished", "Everyone")
        + Csv.Line("rose@example.com", "My first job", "I worked at the five-and-dime on Main Street for 35 cents an hour.", "", "about 1947", "Akron, Ohio", "", "Lily Smith", "Draft", "Only me");

    private string PathFor(Guid fileId) => Path.Combine(options.UploadsDir, $"stories-{fileId:N}.csv");
    private int CurrentYear => clock.GetUtcNow().Year;

    public static string TitleFromBody(string body)
    {
        var first = body.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "";
        var end = first.IndexOfAny(['.', '!', '?']);
        if (end > 0) first = first[..end];
        if (first.Length <= 60) return first;
        var cut = first[..60].LastIndexOf(' ');
        return first[..(cut > 20 ? cut : 60)] + "…";
    }

    public async Task<StoryImportPreview> PreviewAsync(Stream upload)
    {
        Directory.CreateDirectory(options.UploadsDir);
        var fileId = Guid.CreateVersion7();
        string text;
        using (var reader = new StreamReader(upload, detectEncodingFromByteOrderMarks: true))
            text = await reader.ReadToEndAsync();
        await File.WriteAllTextAsync(PathFor(fileId), text);
        return await BuildPreviewAsync(fileId, text);
    }

    public async Task<StoryImportPreview?> LoadPreviewAsync(Guid fileId)
    {
        var path = PathFor(fileId);
        return File.Exists(path) ? await BuildPreviewAsync(fileId, await File.ReadAllTextAsync(path)) : null;
    }

    private async Task<StoryImportPreview> BuildPreviewAsync(Guid fileId, string text)
    {
        var (rows, unknown) = Csv.Read(text, Columns);
        var users = await db.Users.AsNoTracking().ToListAsync();
        var tree = await db.People.AsNoTracking().Where(p => p.LinkedUserId != null).ToListAsync();
        var prompts = await db.Prompts.AsNoTracking().Where(p => p.IsActive && p.ForUserId == null).ToListAsync();
        var existing = await db.Memories.AsNoTracking()
            .Where(m => m.Status != MemoryStatus.NotStarted && m.Body != "")
            .Select(m => new { m.StorytellerId, m.PromptId, m.Title, m.Status }).ToListAsync();
        var seen = new HashSet<(Guid, string)>();

        AppUser? FindUser(string? typed)
        {
            if (string.IsNullOrWhiteSpace(typed)) return null;
            var t = typed.Trim();
            if (t.Contains('@')) return users.FirstOrDefault(u => string.Equals(u.Email, t, StringComparison.OrdinalIgnoreCase));
            var byName = users.Where(u => string.Equals(u.DisplayName, t, StringComparison.OrdinalIgnoreCase)).ToList();
            if (byName.Count == 1) return byName[0];
            // "Rose Smith" or "Grandma Rose" from the family tree, when that person signs in.
            var person = tree.Where(p => string.Equals(p.DisplayName, t, StringComparison.OrdinalIgnoreCase)
                                      || string.Equals(p.Nickname, t, StringComparison.OrdinalIgnoreCase)).ToList();
            return person.Count == 1 ? users.FirstOrDefault(u => u.Id == person[0].LinkedUserId) : null;
        }
        static string Q(string s) => s.Trim().TrimEnd('?', '.', ' ').ToLowerInvariant();

        var result = new List<StoryImportRow>();
        foreach (var row in rows.Take(MaxRows))
        {
            var notes = new List<string>();
            var (when, understood) = DateText.Parse(row.Get("when"), CurrentYear);
            if (!understood) notes.Add($"\"{row.Get("when")}\" is kept as words; it won't place the story on the timeline.");
            var status = row.Get("status")?.ToLowerInvariant();
            var visibility = row.Get("visibility")?.ToLowerInvariant() switch
            {
                null => (Visibility?)null,
                "only me" or "private" or "just me" or "only the storyteller" or "storyteller only" or "me" => Visibility.Private,
                _ => Visibility.AllFamily,
            };
            var line = new StoryLine(row.Line, row.Get("storyteller") ?? "", row.Get("title"), row.Get("body") ?? "", row.Get("question"),
                when, row.Get("place"), Csv.Names(row.Get("people")), row.Get("writtenby"),
                Finished: status is null || !(status.StartsWith("draft") || status is "no" or "unfinished" or "not finished" or "in progress"),
                visibility);

            var teller = FindUser(line.StorytellerText);
            var writer = FindUser(line.WrittenByText);
            if (line.WrittenByText is not null && writer is null) notes.Add($"Couldn't tell who \"{line.WrittenByText}\" is, so the story won't say who wrote it down.");
            Prompt? prompt = null;
            if (line.Question is { } q)
            {
                prompt = prompts.FirstOrDefault(p => Q(p.Question) == Q(q) || Q(p.Title) == Q(q));
                if (prompt is null) notes.Add("That question isn't on the question list, so this will be a story of their own.");
            }

            string? problem = line.Body.Length == 0 ? "There's no story in this row."
                : line.StorytellerText.Length == 0 ? "The Storyteller column is empty."
                : teller is null ? $"No one called \"{line.StorytellerText}\" signs in here. Add them under People first (an email address is needed), or type their email address in this column."
                : line.Body.Length > MemoryService.MaxBodyLength ? "This story is too long for one story (over 200,000 characters)."
                : null;

            string? duplicate = null;
            if (teller is not null && problem is null)
            {
                var title = line.Title ?? prompt?.Title ?? TitleFromBody(line.Body);
                if (prompt is not null && existing.Any(m => m.StorytellerId == teller.Id && m.PromptId == prompt.Id))
                    duplicate = $"{teller.DisplayName} has already answered \"{prompt.Title}\".";
                else if (existing.Any(m => m.StorytellerId == teller.Id && string.Equals(m.Title, title, StringComparison.OrdinalIgnoreCase)))
                    duplicate = $"{teller.DisplayName} already has a story called \"{title}\".";
                else if (!seen.Add((teller.Id, title.ToLowerInvariant())))
                    duplicate = "The same story is on an earlier row.";
            }
            result.Add(new StoryImportRow(line, teller, writer, prompt, duplicate, notes, problem));
        }
        if (rows.Count > MaxRows) unknown = [.. unknown, $"(only the first {MaxRows} rows are read)"];
        return new StoryImportPreview(fileId, result, unknown);
    }

    /// <param name="decisions">Per line number: "import" or "skip". Rows not listed are imported unless they look like a duplicate.</param>
    public async Task<StoryImportResult?> CommitAsync(Guid fileId, IReadOnlyDictionary<string, string> decisions)
    {
        var path = PathFor(fileId);
        if (!File.Exists(path)) return null;
        var preview = await BuildPreviewAsync(fileId, await File.ReadAllTextAsync(path));
        var warnings = new List<string>();
        int imported = 0, skipped = 0, tagged = 0;
        var tree = await db.People.AsNoTracking().ToListAsync();

        foreach (var row in preview.Rows)
        {
            var s = row.Story;
            var choice = decisions.GetValueOrDefault(s.Line.ToString(), row.Duplicate is null ? "import" : "skip");
            if (row.Problem is not null || row.Storyteller is null || choice != "import") { skipped++; continue; }
            var tellerId = row.Storyteller.Id;
            var writerId = row.WrittenBy?.Id ?? tellerId;   // the words are the storyteller's own unless the sheet says who wrote them down

            Memory memory;
            if (row.Prompt is not null)
            {
                memory = await memories.GetOrCreateForPromptAsync(tellerId, row.Prompt, writerId);
                if (memory.Body.Length > 0)
                {
                    warnings.Add($"Line {s.Line}: {row.Storyteller.DisplayName} had already started \"{row.Prompt.Title}\", so this row was skipped.");
                    skipped++;
                    continue;
                }
            }
            else memory = await memories.CreateFreeFormAsync(tellerId, writerId);

            await memories.SaveAsync(memory.Id, new MemoryEdit(row.Title, s.Body, s.When, s.Place), writerId, VersionKind.Save);
            RawMaterial.Keep(db, memory.Id, SourceKind.Import, s.Body, writerId, clock.GetUtcNow().UtcDateTime);
            if (s.Finished) await memories.SetStatusAsync(memory.Id, MemoryStatus.Complete, writerId);
            if (s.Visibility is { } visibility)
                await db.Memories.Where(m => m.Id == memory.Id).ExecuteUpdateAsync(x => x.SetProperty(m => m.Visibility, visibility));

            var tags = new List<Guid>();
            foreach (var name in s.People)
            {
                var found = tree.Where(t => string.Equals(t.DisplayName, name, StringComparison.OrdinalIgnoreCase)
                                         || string.Equals(t.Nickname, name, StringComparison.OrdinalIgnoreCase)).ToList();
                if (found.Count == 1) tags.Add(found[0].Id);
                else warnings.Add($"Line {s.Line}: couldn't find \"{name}\" in the family tree, so they weren't tagged in \"{row.Title}\".");
            }
            if (tags.Count > 0) { await people.SetMemoryPeopleAsync(memory.Id, tags); tagged += tags.Count; }
            imported++;
        }

        audit.Add("stories.import", $"Imported stories from a spreadsheet: {imported} added, {skipped} skipped");
        await db.SaveChangesAsync();
        try { File.Delete(path); } catch (IOException) { }
        return new StoryImportResult(imported, skipped, tagged, warnings);
    }
}
