using FamilyMemories.Admin;
using FamilyMemories.Configuration;
using FamilyMemories.Data;
using Microsoft.EntityFrameworkCore;

namespace FamilyMemories.Family;

public sealed record ImportRow(GedcomIndividual Individual, Person? Match, string MatchReason);

public sealed record ImportPreview(Guid FileId, IReadOnlyList<ImportRow> Rows, int FamilyCount)
{
    public int EventCount { get; init; }
    public int SourceCount { get; init; }
    public int LinkCount { get; init; }
    /// <summary>People the program exported as "(Living)" with no name, for privacy; kept so the tree still connects.</summary>
    public int Placeholders { get; init; }
}

public sealed record ImportResult(int Created, int Matched, int Skipped, int Relationships)
{
    public int Facts { get; init; }
    public int Updated { get; init; }
}

/// <summary>
/// GEDCOM import in two steps: <see cref="PreviewAsync"/> shows every person and flags likely duplicates
/// of people already in the tree; <see cref="CommitAsync"/> applies the admin's choices
/// ("add as new", "same as …", "skip").
/// </summary>
public sealed class GedcomImporter(AppDbContext db, PeopleService people, AuditLog audit, AppOptions options, TimeProvider clock)
{
    public const long MaxFileBytes = 20 * 1024 * 1024;

    private string PathFor(Guid fileId) => Path.Combine(options.UploadsDir, $"gedcom-{fileId:N}.ged");

    public async Task<ImportPreview> PreviewAsync(Stream upload)
    {
        Directory.CreateDirectory(options.UploadsDir);
        var fileId = Guid.CreateVersion7();
        string text;
        using (var reader = new StreamReader(upload, detectEncodingFromByteOrderMarks: true))
            text = await reader.ReadToEndAsync();
        await File.WriteAllTextAsync(PathFor(fileId), text);
        return await BuildPreviewAsync(fileId, Gedcom.Parse(text));
    }

    public async Task<ImportPreview?> LoadPreviewAsync(Guid fileId)
    {
        var path = PathFor(fileId);
        return File.Exists(path) ? await BuildPreviewAsync(fileId, Gedcom.Parse(await File.ReadAllTextAsync(path))) : null;
    }

    private async Task<ImportPreview> BuildPreviewAsync(Guid fileId, GedcomData data)
    {
        var existing = await db.People.AsNoTracking().ToListAsync();
        var rows = data.Individuals.Select(i =>
        {
            var (match, reason) = FindDuplicate(i, existing);
            return new ImportRow(i, match, reason);
        }).ToList();
        var facts = data.Individuals.SelectMany(i => i.Facts).Concat(data.Families.SelectMany(f => f.Facts)).ToList();
        return new ImportPreview(fileId, rows, data.Families.Count)
        {
            EventCount = facts.Count(f => f.Kind != "SOUR"),
            SourceCount = facts.SelectMany(f => f.Citations).Select(c => c.Title).Distinct().Count(),
            LinkCount = data.Individuals.Sum(i => i.Links.Count),
            Placeholders = data.Individuals.Count(i => IsPrivacyPlaceholder(i.Given, i.Surname)),
        };
    }

    /// <summary>Same GEDCOM id from an earlier import; or same first name and surname (or maiden name) with no conflicting birth year.</summary>
    internal static (Person?, string) FindDuplicate(GedcomIndividual i, IReadOnlyList<Person> existing)
    {
        if (existing.FirstOrDefault(p => p.GedcomId == i.Xref) is { } byId) return (byId, "imported before");
        return FindDuplicate(i.Given, i.Surname, i.Birth.Year, existing);
    }

    /// <summary>Same first name and surname (or maiden name) with no conflicting birth year. Also used by the spreadsheet import.</summary>
    /// <summary>"(Living)", "Living", "Private": a person a genealogy program exported without a name, for privacy.</summary>
    public static bool IsPrivacyPlaceholder(string givenNames, string surname) =>
        surname.Trim().Length == 0 && givenNames.Trim().Trim('(', ')', '[', ']').ToLowerInvariant() is "living" or "private" or "hidden";

    internal static (Person?, string) FindDuplicate(string givenNames, string surname, int? birthYear, IEnumerable<Person> existing)
    {
        // Unnamed placeholders can only be matched by their id in the file, never by "name".
        if (IsPrivacyPlaceholder(givenNames, surname)) return (null, "");
        static string First(string s) => s.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.ToLowerInvariant() ?? "";
        var given = First(givenNames);
        var last = surname.Trim().ToLowerInvariant();
        if (given.Length == 0) return (null, "");
        var match = existing.FirstOrDefault(p =>
            First(p.GivenNames) == given
            && (p.Surname.ToLowerInvariant() == last || (p.BirthSurname ?? "").ToLowerInvariant() == last)
            && (p.BirthYear is null || birthYear is null || Math.Abs(p.BirthYear.Value - birthYear.Value) <= 1));
        return match is null ? (null, "") : (match, "same name" + (match.BirthYear is not null && birthYear is not null ? " and birth year" : ""));
    }

    /// <param name="decisions">Per GEDCOM xref: "new", "skip", or a person id meaning "same as this person".</param>
    /// <param name="mainTree">
    /// The file comes from the family's main genealogy program: for matched people its names, dates and places win, and its
    /// life events, sources and links replace the ones imported before. Stories, photos, accounts and hand-added links stay.
    /// </param>
    public async Task<ImportResult?> CommitAsync(Guid fileId, IReadOnlyDictionary<string, string> decisions, bool mainTree = false)
    {
        var path = PathFor(fileId);
        if (!File.Exists(path)) return null;
        var data = Gedcom.Parse(await File.ReadAllTextAsync(path));
        var map = new Dictionary<string, Guid>();
        int created = 0, matched = 0, skipped = 0, links = 0, updated = 0;
        var now = clock.GetUtcNow().UtcDateTime;
        var withFacts = new List<(GedcomIndividual Individual, Guid PersonId)>();

        foreach (var i in data.Individuals)
        {
            var choice = decisions.GetValueOrDefault(i.Xref, "new");
            if (choice == "skip") { skipped++; continue; }
            if (Guid.TryParse(choice, out var existingId) && await db.People.AnyAsync(p => p.Id == existingId))
            {
                map[i.Xref] = existingId;
                var p = await db.People.SingleAsync(x => x.Id == existingId);
                if (mainTree)
                {
                    // The main tree wins wherever it says something; blanks in the file don't erase what's here.
                    p.GedcomId = i.Xref;
                    if (i.Given.Length > 0) p.GivenNames = i.Given;
                    if (i.Surname.Length > 0) p.Surname = i.Surname;
                    p.Nickname = i.Nickname ?? p.Nickname;
                    if (i.Sex != Sex.Unknown) p.Sex = i.Sex;
                    if (i.Birth.HasDate) (p.BirthYear, p.BirthMonth, p.BirthDay, p.BirthCirca) = (i.Birth.Year, i.Birth.Month, i.Birth.Day, i.Birth.Circa);
                    p.BirthPlace = i.BirthPlace ?? p.BirthPlace;
                    if (i.Deceased)
                    {
                        p.IsLiving = false;
                        if (i.Death.HasDate) (p.DeathYear, p.DeathMonth, p.DeathDay, p.DeathCirca) = (i.Death.Year, i.Death.Month, i.Death.Day, i.Death.Circa);
                        p.DeathPlace = i.DeathPlace ?? p.DeathPlace;
                    }
                    p.UpdatedAt = now;
                    updated++;
                    withFacts.Add((i, existingId));
                }
                else
                {
                    p.GedcomId ??= i.Xref;
                    // Fill gaps only; never overwrite what the family already typed.
                    p.BirthYear ??= i.Birth.Year; p.BirthMonth ??= i.Birth.Month; p.BirthDay ??= i.Birth.Day;
                    p.BirthPlace ??= i.BirthPlace;
                    if (i.Deceased && p.IsLiving) { p.IsLiving = false; p.DeathYear ??= i.Death.Year; p.DeathPlace ??= i.DeathPlace; }
                    // Events and sources are added once; after that, only a "main tree" import replaces them.
                    if (!await db.PersonFacts.AnyAsync(f => f.PersonId == existingId && f.FromGedcom)) withFacts.Add((i, existingId));
                }
                matched++;
                continue;
            }

            var person = new Person
            {
                Id = Guid.CreateVersion7(), GivenNames = i.Given, Surname = i.Surname, Nickname = i.Nickname, Sex = i.Sex,
                BirthYear = i.Birth.Year, BirthMonth = i.Birth.Month, BirthDay = i.Birth.Day, BirthCirca = i.Birth.Circa, BirthPlace = i.BirthPlace,
                IsLiving = !i.Deceased,
                DeathYear = i.Death.Year, DeathMonth = i.Death.Month, DeathDay = i.Death.Day, DeathCirca = i.Death.Circa, DeathPlace = i.DeathPlace,
                GedcomId = i.Xref, CreatedAt = now, UpdatedAt = now,
            };
            db.People.Add(person);
            map[i.Xref] = person.Id;
            withFacts.Add((i, person.Id));
            created++;
        }
        await db.SaveChangesAsync();

        foreach (var f in data.Families)
        {
            Guid? h = f.Husband is not null && map.TryGetValue(f.Husband, out var hid) ? hid : null;
            Guid? w = f.Wife is not null && map.TryGetValue(f.Wife, out var wid) ? wid : null;
            if (h is not null && w is not null)
            {
                await people.AddPartnerAsync(h.Value, w.Value, f.Divorced ? PartnershipKind.Divorced : PartnershipKind.Married, f.MarriageYear, null);
                links++;
            }
            foreach (var c in f.Children)
            {
                if (!map.TryGetValue(c, out var childId)) continue;
                foreach (var parent in new[] { h, w }.Where(x => x is not null))
                {
                    if (await people.AddParentAsync(childId, parent!.Value, ParentKind.Biological) is null) links++;
                }
            }
        }

        var factCount = await ReplaceImportedFactsAsync(data, map, withFacts);

        audit.Add("gedcom.import", mainTree
            ? $"Updated {updated} people from the main tree ({created} new, {skipped} skipped, {links} relationships, {factCount} life events and notes)"
            : $"Imported a GEDCOM file: {created} new, {matched} matched, {skipped} skipped, {links} relationships, {factCount} life events and notes");
        await db.SaveChangesAsync();
        try { File.Delete(path); } catch (IOException) { }
        return new ImportResult(created, matched, skipped, links) { Facts = factCount, Updated = updated };
    }

    /// <summary>
    /// Life events (the person's own and their marriages), notes, sources and links from the file, replacing earlier imported
    /// ones for these people. Facts and links the family added by hand are never touched.
    /// </summary>
    private async Task<int> ReplaceImportedFactsAsync(GedcomData data, IReadOnlyDictionary<string, Guid> map, List<(GedcomIndividual Individual, Guid PersonId)> people)
    {
        if (people.Count == 0) return 0;
        var ids = people.Select(x => x.PersonId).ToList();
        await db.PersonFacts.Where(f => ids.Contains(f.PersonId) && f.FromGedcom).ExecuteDeleteAsync();
        await db.PersonLinks.Where(l => ids.Contains(l.PersonId) && l.FromGedcom).ExecuteDeleteAsync();
        var names = data.Individuals.ToDictionary(i => i.Xref, i => i.DisplayName);
        var count = 0;

        foreach (var (i, personId) in people)
        {
            var facts = i.Facts.ToList();
            var partnerOf = new Dictionary<GedcomFact, Guid?>(ReferenceEqualityComparer.Instance);
            foreach (var fam in data.Families.Where(f => f.Husband == i.Xref || f.Wife == i.Xref))
            {
                var partner = fam.Husband == i.Xref ? fam.Wife : fam.Husband;
                var with = partner is not null && names.TryGetValue(partner, out var n) ? $"with {n}" : null;
                foreach (var f in fam.Facts.Select(f => f with { Value = f.Value ?? with }))
                {
                    facts.Add(f);
                    partnerOf[f] = partner is not null && map.TryGetValue(partner, out var pid) ? pid : null;
                }
            }
            if (i.Note is { } note) facts.Add(new GedcomFact("NOTE", "Notes", default, null, null, note, []));

            var order = 0;
            foreach (var f in facts.OrderBy(f => f.Kind is "NOTE" or "SOUR" ? 1 : 0).ThenBy(f => f.Date.SortDate ?? DateOnly.MaxValue))
            {
                db.PersonFacts.Add(new PersonFact
                {
                    Id = Guid.CreateVersion7(), PersonId = personId, Kind = f.Kind, Label = Trim(f.Label, 120)!,
                    Year = f.Date.Year, Month = f.Date.Month, Day = f.Date.Day, Circa = f.Date.Circa, DateWords = Trim(f.Date.Label, 200),
                    Place = Trim(f.Place, 300), Value = Trim(f.Value, 4000), Note = Trim(f.Note, 200_000),
                    Citations = [.. f.Citations.Take(50)], SortOrder = order++, FromGedcom = true,
                    PartnerId = partnerOf.GetValueOrDefault(f),
                });
                count++;
            }
            var linkOrder = 0;
            foreach (var l in i.Links)
                db.PersonLinks.Add(new PersonLink { Id = Guid.CreateVersion7(), PersonId = personId, Label = Trim(l.Label, 120)!, Url = Trim(l.Url, 1000)!, SortOrder = linkOrder++, FromGedcom = true });
        }
        await db.SaveChangesAsync();
        return count;
    }

    private static string? Trim(string? s, int max) => s is null ? null : s.Length > max ? s[..max] : s;

    public async Task<string> ExportAsync()
    {
        var all = await db.People.AsNoTracking().OrderBy(p => p.Surname).ThenBy(p => p.GivenNames).ToListAsync();
        var parents = await db.ParentChildren.AsNoTracking().ToListAsync();
        var partners = await db.Partnerships.AsNoTracking().ToListAsync();
        var facts = await db.PersonFacts.AsNoTracking().ToListAsync();
        var links = await db.PersonLinks.AsNoTracking().ToListAsync();
        return Gedcom.Export(all, parents, partners, options.AppName, facts, links);
    }
}
