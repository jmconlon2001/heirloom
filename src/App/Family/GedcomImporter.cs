using FamilyMemories.Admin;
using FamilyMemories.Configuration;
using FamilyMemories.Data;
using Microsoft.EntityFrameworkCore;

namespace FamilyMemories.Family;

public sealed record ImportRow(GedcomIndividual Individual, Person? Match, string MatchReason);

public sealed record ImportPreview(Guid FileId, IReadOnlyList<ImportRow> Rows, int FamilyCount);

public sealed record ImportResult(int Created, int Matched, int Skipped, int Relationships);

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
        return new ImportPreview(fileId, rows, data.Families.Count);
    }

    /// <summary>Same GEDCOM id from an earlier import; or same first name and surname (or maiden name) with no conflicting birth year.</summary>
    internal static (Person?, string) FindDuplicate(GedcomIndividual i, IReadOnlyList<Person> existing)
    {
        if (existing.FirstOrDefault(p => p.GedcomId == i.Xref) is { } byId) return (byId, "imported before");
        static string First(string s) => s.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.ToLowerInvariant() ?? "";
        var given = First(i.Given);
        var surname = i.Surname.ToLowerInvariant();
        if (given.Length == 0) return (null, "");
        var match = existing.FirstOrDefault(p =>
            First(p.GivenNames) == given
            && (p.Surname.ToLowerInvariant() == surname || (p.BirthSurname ?? "").ToLowerInvariant() == surname)
            && (p.BirthYear is null || i.Birth.Year is null || Math.Abs(p.BirthYear.Value - i.Birth.Year.Value) <= 1));
        return match is null ? (null, "") : (match, "same name" + (match.BirthYear is not null && i.Birth.Year is not null ? " and birth year" : ""));
    }

    /// <param name="decisions">Per GEDCOM xref: "new", "skip", or a person id meaning "same as this person".</param>
    public async Task<ImportResult?> CommitAsync(Guid fileId, IReadOnlyDictionary<string, string> decisions)
    {
        var path = PathFor(fileId);
        if (!File.Exists(path)) return null;
        var data = Gedcom.Parse(await File.ReadAllTextAsync(path));
        var map = new Dictionary<string, Guid>();
        int created = 0, matched = 0, skipped = 0, links = 0;
        var now = clock.GetUtcNow().UtcDateTime;

        foreach (var i in data.Individuals)
        {
            var choice = decisions.GetValueOrDefault(i.Xref, "new");
            if (choice == "skip") { skipped++; continue; }
            if (Guid.TryParse(choice, out var existingId) && await db.People.AnyAsync(p => p.Id == existingId))
            {
                map[i.Xref] = existingId;
                var p = await db.People.SingleAsync(x => x.Id == existingId);
                p.GedcomId ??= i.Xref;
                // Fill gaps only; never overwrite what the family already typed.
                p.BirthYear ??= i.Birth.Year; p.BirthMonth ??= i.Birth.Month; p.BirthDay ??= i.Birth.Day;
                p.BirthPlace ??= i.BirthPlace;
                if (i.Deceased && p.IsLiving) { p.IsLiving = false; p.DeathYear ??= i.Death.Year; p.DeathPlace ??= i.DeathPlace; }
                matched++;
                continue;
            }

            var person = new Person
            {
                Id = Guid.CreateVersion7(), GivenNames = i.Given, Surname = i.Surname, Nickname = i.Nickname, Sex = i.Sex,
                BirthYear = i.Birth.Year, BirthMonth = i.Birth.Month, BirthDay = i.Birth.Day, BirthCirca = i.Birth.Circa, BirthPlace = i.BirthPlace,
                IsLiving = !i.Deceased,
                DeathYear = i.Death.Year, DeathMonth = i.Death.Month, DeathDay = i.Death.Day, DeathCirca = i.Death.Circa, DeathPlace = i.DeathPlace,
                Bio = i.Note, GedcomId = i.Xref, CreatedAt = now, UpdatedAt = now,
            };
            db.People.Add(person);
            map[i.Xref] = person.Id;
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

        audit.Add("gedcom.import", $"Imported a GEDCOM file: {created} new, {matched} matched, {skipped} skipped, {links} relationships");
        await db.SaveChangesAsync();
        try { File.Delete(path); } catch (IOException) { }
        return new ImportResult(created, matched, skipped, links);
    }

    public async Task<string> ExportAsync()
    {
        var all = await db.People.AsNoTracking().OrderBy(p => p.Surname).ThenBy(p => p.GivenNames).ToListAsync();
        var parents = await db.ParentChildren.AsNoTracking().ToListAsync();
        var partners = await db.Partnerships.AsNoTracking().ToListAsync();
        return Gedcom.Export(all, parents, partners, options.AppName);
    }
}
