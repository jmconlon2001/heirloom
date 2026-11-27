using System.Diagnostics;
using System.Net;
using System.Text.RegularExpressions;
using FamilyMemories.Data;
using FamilyMemories.Family;
using FamilyMemories.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace FamilyMemories.Tests;

/// <summary>
/// Checks the GEDCOM import against a real family's file. Point HEIRLOOM_GEDCOM_SAMPLE at a .ged exported from Reunion (or any
/// genealogy program); without it these tests are skipped. The file is never copied into the repository.
/// Expectations come from <see cref="RawGedcom"/>, a deliberately simple, separate reading of the file, not from the importer.
/// </summary>
public sealed class RealGedcomTests(PostgresFixture postgres, ITestOutputHelper output) : IAsyncLifetime
{
    private static readonly string? SamplePath = Environment.GetEnvironmentVariable("HEIRLOOM_GEDCOM_SAMPLE");
    private readonly AppFactory app = new(postgres);
    public ValueTask InitializeAsync() => app.InitializeAsync();
    public ValueTask DisposeAsync() => app.DisposeAsync();

    private static string Sample()
    {
        Assert.SkipWhen(string.IsNullOrEmpty(SamplePath) || !File.Exists(SamplePath), "Set HEIRLOOM_GEDCOM_SAMPLE to a .ged file to run this.");
        return File.ReadAllText(SamplePath!);
    }

    [Fact]
    public void The_reader_agrees_with_a_plain_reading_of_the_file_person_by_person()
    {
        var text = Sample();
        var raw = RawGedcom.Read(text);
        var data = Gedcom.Parse(text);
        output.WriteLine($"{raw.People.Count} people, {raw.Families.Count} families, {raw.People.Sum(p => p.Value.Events.Count)} events");

        Assert.Equal(raw.People.Count, data.Individuals.Count);
        Assert.Equal(raw.Families.Count, data.Families.Count);
        var problems = new List<string>();
        foreach (var i in data.Individuals)
        {
            var r = raw.People[i.Xref];
            var mine = i.Facts.Where(f => f.Kind != "SOUR").ToList();
            // Every supported event, with its place, in the same order.
            var expected = r.Events.Select(e => $"{e.Tag}|{e.Place}").ToList();
            var actual = mine.Select(f => $"{f.Kind}|{f.Place}").ToList();
            if (!expected.SequenceEqual(actual)) { problems.Add($"{i.Xref} events: expected [{string.Join(", ", expected)}] got [{string.Join(", ", actual)}]"); continue; }
            for (var k = 0; k < mine.Count; k++)
            {
                var e = r.Events[k];
                var f = mine[k];
                if (f.Citations.Count != e.Citations) problems.Add($"{i.Xref} {e.Tag}: {e.Citations} citations in the file, {f.Citations.Count} read");
                if (e.HasNote && string.IsNullOrWhiteSpace(f.Note)) problems.Add($"{i.Xref} {e.Tag}: the note was lost");
                if (e.Year is { } y && f.Date.Year != y) problems.Add($"{i.Xref} {e.Tag}: year {y} in \"{e.Date}\", read {f.Date.Year}");
                if (e.Month is { } m && e.Year is not null && f.Date.Month != m) problems.Add($"{i.Xref} {e.Tag}: month {m} in \"{e.Date}\", read {f.Date.Month}");
                if (e.Day is { } d && e.Year is not null && f.Date.Day != d) problems.Add($"{i.Xref} {e.Tag}: day {d} in \"{e.Date}\", read {f.Date.Day}");
                if (e.Date is { Length: > 0 } && f.Date.IsEmpty) problems.Add($"{i.Xref} {e.Tag}: the date \"{e.Date}\" was lost");
                if (e.Value is { } v && f.Value != v && f.Note?.Contains(v) != true) problems.Add($"{i.Xref} {e.Tag}: value \"{v}\" read as \"{f.Value}\"");
            }
            var general = i.Facts.Where(f => f.Kind == "SOUR").SelectMany(f => f.Citations).Count();
            if (general != r.PersonCitations) problems.Add($"{i.Xref}: {r.PersonCitations} citations on the person and name, {general} read");
            if (r.FamilySearchId is { } fs && i.Links.All(l => !l.Url.EndsWith("/" + fs))) problems.Add($"{i.Xref}: FamilySearch ID {fs} not linked");
            if (r.HasNote && i.Note is null) problems.Add($"{i.Xref}: the person's note was lost");
        }
        foreach (var f in data.Families)
        {
            var r = raw.Families[f.Xref];
            if (f.Children.Count != r.Children.Count || f.Husband != r.Husband || f.Wife != r.Wife) problems.Add($"{f.Xref}: members differ");
            if (f.Facts.Count != r.Events) problems.Add($"{f.Xref}: {r.Events} couple events in the file, {f.Facts.Count} read");
        }
        foreach (var p in problems.Take(40)) output.WriteLine(p);
        Assert.True(problems.Count == 0, $"{problems.Count} differences; first: {problems.FirstOrDefault()}");
    }

    [Fact]
    public async Task Importing_it_through_the_admin_pages_builds_exactly_that_tree_and_a_main_tree_reimport_changes_nothing()
    {
        var text = Sample();
        var raw = RawGedcom.Read(text);
        var data = Gedcom.Parse(text);
        var admin = await Browser.SignedInAsync(app, AppFactory.AdminEmail);

        async Task<(string Preview, TimeSpan Took)> ImportAsync(bool mainTree)
        {
            var page = await admin.Http.GetStringAsync("/admin/gedcom");
            var upload = new MultipartFormDataContent { { new StringContent(HtmlForms.AntiforgeryToken(page)), "__RequestVerificationToken" } };
            upload.Add(new ByteArrayContent(File.ReadAllBytes(SamplePath!)), "File", Path.GetFileName(SamplePath!));
            var preview = WebUtility.HtmlDecode(await (await admin.Http.PostAsync("/admin/gedcom?handler=Upload", upload)).Content.ReadAsStringAsync());
            var fileId = Regex.Match(preview, "name=\"FileId\" value=\"([^\"]+)\"").Groups[1].Value;
            var fields = new List<(string, string)> { ("FileId", fileId) };
            if (mainTree) fields.Add(("MainTree", "true"));
            foreach (Match m in Regex.Matches(preview, "name=\"Decisions\\[(@[^\\]]+@)\\]\">\\s*<option value=\"([^\"]+)\""))
                fields.Add(($"Decisions[{m.Groups[1].Value}]", m.Groups[2].Value));
            var clock = Stopwatch.StartNew();
            var response = await admin.Http.PostFormAsync("/admin/gedcom?handler=Commit", preview, [.. fields]);
            Assert.True(response.StatusCode == HttpStatusCode.Redirect, $"Import answered {(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
            return (preview, clock.Elapsed);
        }

        var (first, took) = await ImportAsync(mainTree: false);
        output.WriteLine($"First import: {took.TotalSeconds:0.0}s");
        Assert.Contains($"{raw.People.Count} people and {raw.Families.Count} families found", first);

        var people = await app.WithDbAsync(db => db.People.AsNoTracking().ToListAsync());
        Assert.Equal(raw.People.Count, people.Count);
        Assert.Equal(raw.People.Count, people.Select(p => p.GedcomId).Distinct().Count());
        Assert.Equal(raw.ParentChildPairs, await app.WithDbAsync(db => db.ParentChildren.CountAsync()));
        Assert.Equal(raw.Couples, await app.WithDbAsync(db => db.Partnerships.CountAsync()));

        // Every event, note and source landed on the right person.
        var expectedFacts = ExpectedFacts(data);
        var stored = await app.WithDbAsync(db => db.PersonFacts.AsNoTracking().Include(f => f.Person).ToListAsync());
        Assert.Equal(expectedFacts.Count, stored.Count);
        var byPerson = stored.GroupBy(f => f.Person!.GedcomId!).ToDictionary(g => g.Key, g => g.Select(Describe).Order().ToList());
        foreach (var (xref, facts) in expectedFacts.GroupBy(x => x.Xref).ToDictionary(g => g.Key, g => g.Select(x => x.Describe).Order().ToList()))
            Assert.Equal(facts, byPerson.GetValueOrDefault(xref) ?? []);
        Assert.Equal(data.Individuals.Sum(i => i.Links.Count), await app.WithDbAsync(db => db.PersonLinks.CountAsync()));

        // A year later, the same file as the main tree: everyone matched by id, nothing doubled.
        var (second, took2) = await ImportAsync(mainTree: true);
        output.WriteLine($"Main-tree reimport: {took2.TotalSeconds:0.0}s");
        Assert.Equal(raw.People.Count, Regex.Matches(second, "\\(imported before\\)").Count);
        Assert.Equal(raw.People.Count, await app.WithDbAsync(db => db.People.CountAsync()));
        Assert.Equal(raw.ParentChildPairs, await app.WithDbAsync(db => db.ParentChildren.CountAsync()));
        Assert.Equal(raw.Couples, await app.WithDbAsync(db => db.Partnerships.CountAsync()));
        Assert.Equal(expectedFacts.Count, await app.WithDbAsync(db => db.PersonFacts.CountAsync()));
        Assert.Equal(data.Individuals.Sum(i => i.Links.Count), await app.WithDbAsync(db => db.PersonLinks.CountAsync()));

        // The pages stay quick and whole with a real tree's size.
        var busiest = await app.WithDbAsync(db => db.PersonFacts.GroupBy(f => f.PersonId).OrderByDescending(g => g.Count()).Select(g => g.Key).Take(5).ToListAsync());
        foreach (var url in new[] { "/people", "/tree", "/api/tree", "/library/on-this-day", "/admin/gedcom" }.Concat(busiest.Select(id => $"/people/{id}")))
        {
            var clock = Stopwatch.StartNew();
            var response = await admin.Http.GetAsync(url);
            output.WriteLine($"{url}: {(int)response.StatusCode} in {clock.ElapsedMilliseconds} ms");
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.True(clock.Elapsed < TimeSpan.FromSeconds(5), $"{url} took {clock.Elapsed.TotalSeconds:0.0}s");
        }

        // Exporting and reading it back keeps every person's events, notes, sources and links.
        var exported = Gedcom.Parse(await admin.Http.GetStringAsync("/admin/gedcom?handler=Export"));
        Assert.Equal(raw.People.Count, exported.Individuals.Count);
        Assert.Equal(raw.Couples, exported.Families.Count(f => f.Husband is not null && f.Wife is not null));
        var original = data.Individuals.ToDictionary(i => i.Xref);
        var dbPeople = await app.WithDbAsync(db => db.People.AsNoTracking().OrderBy(p => p.Surname).ThenBy(p => p.GivenNames).ToListAsync());
        var mismatches = new List<string>();
        for (var k = 0; k < dbPeople.Count; k++)
        {
            var before = original[dbPeople[k].GedcomId!];
            var after = exported.Individuals[k];
            var a = before.Facts.Select(DescribeParsed).Order().ToList();
            var b = after.Facts.Select(DescribeParsed).Order().ToList();
            var coupleBefore = data.Families.Where(f => f.Husband == before.Xref || f.Wife == before.Xref).SelectMany(f => f.Facts).Select(DescribeParsed).Order().ToList();
            var coupleAfter = exported.Families.Where(f => f.Husband == after.Xref || f.Wife == after.Xref).SelectMany(f => f.Facts).Select(DescribeParsed).Order().ToList();
            if (!a.SequenceEqual(b)) mismatches.Add($"{before.Xref} {before.DisplayName}: {string.Join(" / ", a.Except(b).Take(2))} ≠ {string.Join(" / ", b.Except(a).Take(2))}");
            if (!coupleBefore.SequenceEqual(coupleAfter)) mismatches.Add($"{before.Xref} couple events: {string.Join(" / ", coupleBefore.Except(coupleAfter).Take(2))}");
            if (before.Note != after.Note)
            {
                var at = Enumerable.Range(0, Math.Min(before.Note?.Length ?? 0, after.Note?.Length ?? 0)).FirstOrDefault(c => before.Note![c] != after.Note![c], -1);
                mismatches.Add($"{before.Xref} note differs at {at} (lengths {before.Note?.Length}/{after.Note?.Length}): «{Around(before.Note, at)}» vs «{Around(after.Note, at)}»");
            }
            if (!before.Links.Select(l => l.Url).Order().SequenceEqual(after.Links.Select(l => l.Url).Order())) mismatches.Add($"{before.Xref} links differ");
        }
        foreach (var m in mismatches.Take(30)) output.WriteLine(m);
        Assert.True(mismatches.Count == 0, $"{mismatches.Count} people changed in the round trip; first: {mismatches.FirstOrDefault()}");
    }

    /// <summary>What the importer should store: each person's events and sources, their couples' events, and their notes.</summary>
    private static List<(string Xref, string Describe)> ExpectedFacts(GedcomData data)
    {
        var names = data.Individuals.ToDictionary(i => i.Xref, i => i.DisplayName);
        var list = new List<(string, string)>();
        foreach (var i in data.Individuals)
        {
            foreach (var f in i.Facts) list.Add((i.Xref, DescribeParsed(f)));
            foreach (var fam in data.Families.Where(f => f.Husband == i.Xref || f.Wife == i.Xref))
            {
                var partner = fam.Husband == i.Xref ? fam.Wife : fam.Husband;
                foreach (var f in fam.Facts)
                    list.Add((i.Xref, DescribeParsed(f with { Value = f.Value ?? (partner is not null && names.TryGetValue(partner, out var n) ? $"with {n}" : null) })));
            }
            if (i.Note is { } note) list.Add((i.Xref, DescribeParsed(new GedcomFact("NOTE", "Notes", default, null, null, note, []))));
        }
        return list;
    }

    private static string DescribeParsed(GedcomFact f) =>
        $"{f.Kind}|{f.Label}|{f.Date.Format()}|{f.Place}|{Clip(f.Value, 4000)}|{Clip(f.Note, 200_000)}|{string.Join(";", f.Citations.Take(50).Select(c => $"{c.Title}/{c.Page}/{c.Url}"))}";

    private static string Describe(PersonFact f) =>
        $"{f.Kind}|{f.Label}|{f.Date.Format()}|{f.Place}|{f.Value}|{f.Note}|{string.Join(";", f.Citations.Select(c => $"{c.Title}/{c.Page}/{c.Url}"))}";

    private static string Around(string? s, int at) => s is null ? "(none)" : s.Substring(Math.Max(0, at - 20), Math.Min(50, s.Length - Math.Max(0, at - 20))).Replace("\n", "⏎");

    private static string? Clip(string? s, int max) => s is null ? null : s.Length > max ? s[..max] : s;
}

/// <summary>
/// A plain, separate reading of a GEDCOM file, for checking the real reader against: lines and levels, nothing clever.
/// </summary>
internal static class RawGedcom
{
    public sealed record Event(string Tag, string? Date, string? Place, int Citations, bool HasNote, string? Value, int? Year, int? Month, int? Day);
    public sealed record PersonRecord(List<Event> Events, int PersonCitations, string? FamilySearchId, bool HasNote);
    public sealed record FamilyRecord(string? Husband, string? Wife, List<string> Children, int Events);
    public sealed record Result(Dictionary<string, PersonRecord> People, Dictionary<string, FamilyRecord> Families, int ParentChildPairs, int Couples);

    private static readonly string[] Months = ["JAN", "FEB", "MAR", "APR", "MAY", "JUN", "JUL", "AUG", "SEP", "OCT", "NOV", "DEC"];

    public static Result Read(string text)
    {
        var lines = new List<(int Level, string Text)>();
        foreach (var l in text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n').Select(l => l.TrimStart()).Where(l => l.Length > 0))
        {
            var p = l.Split(' ', 3);
            var level = int.Parse(p[0]);
            var rest = p.Length > 1 ? l[(p[0].Length + 1)..] : "";
            // CONC joins and CONT breaks a long value: fold them into the line above.
            if (lines.Count > 0 && (rest.StartsWith("CONC") || rest.StartsWith("CONT")) && level == lines[^1].Level + 1)
            {
                var more = rest.Length > 5 ? rest[5..] : "";
                lines[^1] = (lines[^1].Level, lines[^1].Text + (rest.StartsWith("CONT") ? "\n" : "") + more);
                continue;
            }
            lines.Add((level, rest));
        }
        var sources = lines.Where(l => l.Level == 0 && Regex.IsMatch(l.Text, "^@[^@]+@ SOUR")).Select(l => l.Text.Split(' ')[0]).ToHashSet();
        var notes = lines.Where(l => l.Level == 0 && Regex.IsMatch(l.Text, "^@[^@]+@ NOTE")).Select(l => l.Text.Split(' ')[0]).ToHashSet();
        var supported = Gedcom.EventLabels.Keys.Where(k => !Gedcom.FamilyEventTags.Contains(k) || k == "ENGA").ToHashSet();
        var familyTags = new HashSet<string>(["MARR", "MARB", "MARL", "ENGA", "DIV", "ANUL", "SEPA", "EVEN"]);
        // "SOUR @S12@" pointing at a source record that exists, or a source written out inline.
        bool CountsAsCitation(string rest)
        {
            var v = rest.Length > 5 ? rest[5..].Trim() : "";
            return v.StartsWith('@') ? sources.Contains(v) : v.Length > 0;
        }

        var people = new Dictionary<string, PersonRecord>();
        var families = new Dictionary<string, FamilyRecord>();
        for (var i = 0; i < lines.Count; i++)
        {
            if (lines[i].Level != 0) continue;
            var head = lines[i].Text.Split(' ', 3);
            if (head.Length < 2 || !head[0].StartsWith('@')) continue;
            var end = i + 1;
            while (end < lines.Count && lines[end].Level != 0) end++;
            var body = lines.GetRange(i + 1, end - i - 1);
            if (head[1] == "INDI")
            {
                var events = new List<Event>();
                var personSources = new HashSet<string>(); string? fs = null; var hasNote = false;
                for (var k = 0; k < body.Count; k++)
                {
                    if (body[k].Level != 1) continue;
                    var parts = body[k].Text.Split(' ', 2);
                    var tag = parts[0]; var value = parts.Length > 1 ? parts[1].Trim() : "";
                    var sub = body.Skip(k + 1).TakeWhile(x => x.Level > 1).ToList();
                    // The same source and page cited on the person and on the name is one citation.
                    string Key(int at) => body[at].Text + "|" + body.Skip(at + 1).TakeWhile(x => x.Level > body[at].Level).FirstOrDefault(x => x.Text.StartsWith("PAGE ")).Text;
                    if (tag == "SOUR" && CountsAsCitation(body[k].Text)) personSources.Add(Key(k));
                    if (tag == "NAME")
                        for (var j = k + 1; j < body.Count && body[j].Level > 1; j++)
                            if (body[j].Level == 2 && body[j].Text.StartsWith("SOUR ") && CountsAsCitation(body[j].Text)) personSources.Add(Key(j));
                    if (tag == "REFN" && Regex.IsMatch(value, "^[A-Z0-9]{4}-[A-Z0-9]{3,4}$")) fs = value;
                    if (tag == "NOTE" && (value.Length > 0 && (!value.StartsWith('@') || notes.Contains(value)) || sub.Any(x => x.Text.StartsWith("CONT") || x.Text.StartsWith("CONC")))) hasNote = true;
                    if (!supported.Contains(tag)) continue;
                    var date = sub.FirstOrDefault(x => x.Level == 2 && x.Text.StartsWith("DATE ")).Text?[5..].Trim();
                    var place = sub.FirstOrDefault(x => x.Level == 2 && x.Text.StartsWith("PLAC ")).Text?[5..].Trim();
                    var citations = sub.Count(x => x.Level == 2 && x.Text.StartsWith("SOUR ") && CountsAsCitation(x.Text));
                    var eventNote = sub.Any(x => x.Level == 2 && (x.Text.StartsWith("NOTE ") || x.Text.StartsWith("CAUS ")))
                                 || (value.StartsWith('@') && notes.Contains(value));
                    var plainValue = value.Length > 0 && value != "Y" && !value.StartsWith('@') ? value : null;
                    var (y, m, d) = DateParts(date);
                    events.Add(new Event(tag, date, string.IsNullOrEmpty(place) ? null : place, citations, eventNote, tag is "EVEN" ? null : plainValue, y, m, d));
                }
                people[head[0]] = new PersonRecord(events, personSources.Count, fs, hasNote);
            }
            else if (head[1] == "FAM")
            {
                string? h = null, w = null; var children = new List<string>(); var ev = 0;
                foreach (var l in body.Where(x => x.Level == 1))
                {
                    var parts = l.Text.Split(' ', 2);
                    if (parts[0] == "HUSB") h = parts[1].Trim();
                    else if (parts[0] == "WIFE") w = parts[1].Trim();
                    else if (parts[0] == "CHIL") children.Add(parts[1].Trim());
                    else if (familyTags.Contains(parts[0])) ev++;
                }
                families[head[0]] = new FamilyRecord(h, w, children, ev);
            }
        }
        var pairs = families.Values.SelectMany(f => f.Children.SelectMany(c => new[] { f.Husband, f.Wife }.OfType<string>().Select(p => (c, p)))).Distinct().Count();
        var couples = families.Values.Where(f => f.Husband is not null && f.Wife is not null)
            .Select(f => string.CompareOrdinal(f.Husband, f.Wife) < 0 ? (f.Husband, f.Wife) : (f.Wife, f.Husband)).Distinct().Count();
        return new Result(people, families, pairs, couples);
    }

    /// <summary>The year is the first four-digit number; a day and month count only in the plain "12 JUN 1952" or "JUN 1952" form.</summary>
    private static (int?, int?, int?) DateParts(string? date)
    {
        if (string.IsNullOrWhiteSpace(date)) return (null, null, null);
        var year = Regex.Match(date, @"\b(\d{4})\b") is { Success: true } y ? int.Parse(y.Groups[1].Value) : (int?)null;
        var full = Regex.Match(date.ToUpperInvariant(), @"^(?:ABT |BEF |AFT |EST |CAL )?(?:(\d{1,2}) )?(JAN|FEB|MAR|APR|MAY|JUN|JUL|AUG|SEP|OCT|NOV|DEC) \d{4}$");
        if (!full.Success) return (year, null, null);
        return (year, Array.IndexOf(Months, full.Groups[2].Value) + 1, full.Groups[1].Success ? int.Parse(full.Groups[1].Value) : null);
    }
}
