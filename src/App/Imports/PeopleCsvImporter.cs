using FamilyMemories.Admin;
using FamilyMemories.Configuration;
using FamilyMemories.Data;
using FamilyMemories.Family;
using Microsoft.EntityFrameworkCore;

namespace FamilyMemories.Imports;

/// <summary>One person read from the spreadsheet.</summary>
public sealed record PersonLine(
    int Line, string Given, string Surname, string? BirthSurname, string? Nickname, Sex Sex,
    FuzzyDate Birth, string? BirthPlace, bool IsLiving, FuzzyDate Death, string? DeathPlace, string? Bio,
    string? Email, IReadOnlyList<string> Roles, IReadOnlyList<string> Parents, IReadOnlyList<string> Partners)
{
    public string DisplayName => string.Join(' ', new[] { Given, Surname }.Where(s => s.Length > 0));
}

/// <summary>What will happen to a person's sign-in account.</summary>
public enum AccountPlan { None, Create, LinkExisting, Problem }

public sealed record PeopleImportRow(
    PersonLine Person, Person? Match, string MatchReason, AccountPlan Account, string AccountText,
    IReadOnlyList<string> Notes, string? Problem);

public sealed record PeopleImportPreview(Guid FileId, IReadOnlyList<PeopleImportRow> Rows, IReadOnlyList<string> UnknownColumns);

public sealed record PeopleImportResult(int Created, int Matched, int Skipped, int AccountsCreated, int AccountsLinked, int Invitations,
    int Relationships, IReadOnlyList<string> Warnings);

/// <summary>
/// Adds people to the family tree from a spreadsheet, with their parents and partners, and optionally a sign-in
/// account for anyone with an email address. Same shape as the GEDCOM import: upload, check a preview, then import.
/// </summary>
public sealed class PeopleCsvImporter(
    AppDbContext db, PeopleService people, UserAdminService accounts, AuditLog audit, AppOptions options, TimeProvider clock)
{
    public const long MaxFileBytes = 10 * 1024 * 1024;
    public const int MaxRows = 5000;

    /// <summary>The columns we understand, by their loosely-matched header (see <see cref="Csv.NormalizeHeader"/>).</summary>
    public static readonly IReadOnlyDictionary<string, string> Columns = new Dictionary<string, string>
    {
        ["name"] = "name", ["fullname"] = "name",
        ["givennames"] = "given", ["givenname"] = "given", ["firstname"] = "given", ["first"] = "given", ["firstnames"] = "given", ["forename"] = "given",
        ["surname"] = "surname", ["lastname"] = "surname", ["last"] = "surname", ["familyname"] = "surname",
        ["maidenname"] = "birthsurname", ["birthsurname"] = "birthsurname", ["surnameatbirth"] = "birthsurname", ["nee"] = "birthsurname",
        ["nickname"] = "nickname", ["knownas"] = "nickname", ["goesby"] = "nickname",
        ["sex"] = "sex", ["gender"] = "sex",
        ["born"] = "born", ["birth"] = "born", ["birthdate"] = "born", ["dateofbirth"] = "born", ["dob"] = "born", ["birthday"] = "born",
        ["birthplace"] = "birthplace", ["placeofbirth"] = "birthplace", ["bornin"] = "birthplace",
        ["died"] = "died", ["death"] = "died", ["deathdate"] = "died", ["dateofdeath"] = "died",
        ["deathplace"] = "deathplace", ["placeofdeath"] = "deathplace", ["diedin"] = "deathplace",
        ["living"] = "living", ["alive"] = "living", ["isliving"] = "living",
        ["bio"] = "bio", ["about"] = "bio", ["notes"] = "bio", ["note"] = "bio", ["biography"] = "bio",
        ["email"] = "email", ["emailaddress"] = "email", ["email1"] = "email",
        ["role"] = "roles", ["roles"] = "roles", ["access"] = "roles",
        ["parents"] = "parents", ["parent"] = "parents", ["mother"] = "parents", ["father"] = "parents", ["mom"] = "parents", ["dad"] = "parents",
        ["parent1"] = "parents", ["parent2"] = "parents",
        ["partner"] = "partners", ["partners"] = "partners", ["spouse"] = "partners", ["spouses"] = "partners", ["husband"] = "partners",
        ["wife"] = "partners", ["marriedto"] = "partners",
    };

    /// <summary>A ready-to-fill spreadsheet with two example rows.</summary>
    public static string Template() =>
        Csv.Line("First name", "Last name", "Maiden name", "Nickname", "Sex", "Born", "Birthplace", "Died", "Death place", "Parents", "Partner", "Email", "Role", "About")
        + Csv.Line("Rose", "Smith", "Miller", "Grandma Rose", "F", "June 12, 1931", "Akron, Ohio", "", "", "", "Joseph Smith", "rose@example.com", "Storyteller", "Grew roses all her life.")
        + Csv.Line("Joseph", "Smith", "", "Joe", "M", "about 1928", "", "2004-03-03", "Akron, Ohio", "", "Rose Smith", "", "", "")
        + Csv.Line("Ann", "Smith", "", "", "F", "1956", "", "", "", "Joseph Smith; Rose Smith", "", "ann@example.com", "Family", "");

    private string PathFor(Guid fileId) => Path.Combine(options.UploadsDir, $"people-{fileId:N}.csv");
    private int CurrentYear => clock.GetUtcNow().Year;

    public async Task<PeopleImportPreview> PreviewAsync(Stream upload)
    {
        Directory.CreateDirectory(options.UploadsDir);
        var fileId = Guid.CreateVersion7();
        string text;
        using (var reader = new StreamReader(upload, detectEncodingFromByteOrderMarks: true))
            text = await reader.ReadToEndAsync();
        await File.WriteAllTextAsync(PathFor(fileId), text);
        return await BuildPreviewAsync(fileId, text);
    }

    public async Task<PeopleImportPreview?> LoadPreviewAsync(Guid fileId)
    {
        var path = PathFor(fileId);
        return File.Exists(path) ? await BuildPreviewAsync(fileId, await File.ReadAllTextAsync(path)) : null;
    }

    private async Task<PeopleImportPreview> BuildPreviewAsync(Guid fileId, string text)
    {
        var (rows, unknown) = Csv.Read(text, Columns);
        var existing = await db.People.AsNoTracking().ToListAsync();
        var users = await db.Users.AsNoTracking().Select(u => new { u.Id, u.Email, u.DisplayName }).ToListAsync();
        var linked = existing.Where(p => p.LinkedUserId is not null).ToDictionary(p => p.LinkedUserId!.Value);
        var emailsInFile = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var result = new List<PeopleImportRow>();
        foreach (var row in rows.Take(MaxRows))
        {
            var notes = new List<string>();
            var line = ReadLine(row, notes);
            if (line.Given.Length == 0 && line.Surname.Length == 0)
            {
                result.Add(new(line, null, "", AccountPlan.None, "", notes, "No name, so this row can't be imported."));
                continue;
            }
            var (match, reason) = GedcomImporter.FindDuplicate(line.Given, line.Surname, line.Birth.Year, existing);

            var (plan, accountText) = (AccountPlan.None, "");
            if (line.Email is { } email)
            {
                var user = users.FirstOrDefault(u => string.Equals(u.Email, email, StringComparison.OrdinalIgnoreCase));
                if (!email.Contains('@') || email.Contains(' '))
                    (plan, accountText) = (AccountPlan.Problem, $"\"{email}\" isn't a whole email address, so no account will be made.");
                else if (!emailsInFile.Add(email))
                    (plan, accountText) = (AccountPlan.Problem, $"{email} is on an earlier row too, so no account will be made for this one.");
                else if (user is null)
                    (plan, accountText) = (AccountPlan.Create, $"New account for {email} ({string.Join(", ", AccountRoles(line.Roles))})");
                else if (linked.TryGetValue(user.Id, out var other) && other.Id != match?.Id)
                    (plan, accountText) = (AccountPlan.Problem, $"{email} already signs in as {other.DisplayName}, so it won't be linked.");
                else
                    (plan, accountText) = (AccountPlan.LinkExisting, $"Linked to {user.DisplayName}'s account");
            }
            result.Add(new(line, match, reason, plan, accountText, notes, null));
        }
        if (rows.Count > MaxRows) unknown = [.. unknown, $"(only the first {MaxRows} rows are read)"];
        return new PeopleImportPreview(fileId, result, unknown);
    }

    private PersonLine ReadLine(CsvRow row, List<string> notes)
    {
        var given = row.Get("given") ?? "";
        var surname = row.Get("surname") ?? "";
        if (given.Length == 0 && surname.Length == 0 && row.Get("name") is { } full)
        {
            var parts = full.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            (given, surname) = parts.Length == 1 ? (parts[0], "") : (string.Join(' ', parts[..^1]), parts[^1]);
        }

        FuzzyDate Date(string key, string what)
        {
            var (date, understood) = DateText.Parse(row.Get(key), CurrentYear);
            if (understood) return date;
            notes.Add($"Couldn't read the {what} date \"{row.Get(key)}\", so it was left out.");
            return FuzzyDate.Empty;
        }
        var birth = Date("born", "birth");
        var death = Date("died", "death");
        var living = Csv.YesNo(row.Get("living")) ?? (death.IsEmpty && row.Get("died") is null && row.Get("deathplace") is null);

        var sex = row.Get("sex")?.ToLowerInvariant() switch
        {
            "f" or "female" or "woman" or "w" => Sex.Female,
            "m" or "male" or "man" => Sex.Male,
            null => Sex.Unknown,
            _ => Sex.Other,
        };
        var roles = (row.Get("roles") ?? "").Split([',', ';', '/', '&', '+'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .SelectMany(r => r.Split(" and ", StringSplitOptions.TrimEntries))
            .Select(r => r.ToLowerInvariant().TrimEnd('s') switch
            {
                "storyteller" or "teller" => Roles.Storyteller,
                "admin" or "administrator" or "family admin" => Roles.Admin,
                "family" or "reader" or "family member" => Roles.Family,
                _ => null,
            })
            .OfType<string>().Distinct().ToList();

        return new PersonLine(row.Line, given, surname, row.Get("birthsurname"), row.Get("nickname"), sex,
            birth, row.Get("birthplace"), living, living ? FuzzyDate.Empty : death, living ? null : row.Get("deathplace"),
            row.Get("bio"), row.Get("email"), roles, Csv.Names(row.Get("parents")), Csv.Names(row.Get("partners")));
    }

    private static IReadOnlyList<string> AccountRoles(IReadOnlyList<string> roles) =>
        [.. Roles.All.Where(r => r == Roles.Family || roles.Contains(r))];

    /// <param name="decisions">Per line number: "new", "skip", or a person id meaning "same as this person".</param>
    public async Task<PeopleImportResult?> CommitAsync(Guid fileId, IReadOnlyDictionary<string, string> decisions, bool sendInvitations, string inviterName)
    {
        var path = PathFor(fileId);
        if (!File.Exists(path)) return null;
        var preview = await BuildPreviewAsync(fileId, await File.ReadAllTextAsync(path));
        var warnings = new List<string>();
        var byLine = new Dictionary<int, Guid>();
        int created = 0, matched = 0, skipped = 0, accountsCreated = 0, accountsLinked = 0, invitations = 0, links = 0;
        var coreSet = await db.PromptSets.Where(s => s.Kind == PromptSetKind.Core).Select(s => (Guid?)s.Id).FirstOrDefaultAsync();

        foreach (var row in preview.Rows)
        {
            var p = row.Person;
            var choice = decisions.GetValueOrDefault(p.Line.ToString(), row.Match is null ? "new" : row.Match.Id.ToString());
            if (row.Problem is not null || choice == "skip") { skipped++; continue; }

            Person person;
            if (Guid.TryParse(choice, out var existingId) && await db.People.SingleOrDefaultAsync(x => x.Id == existingId) is { } found)
            {
                person = found;
                // Fill gaps only; never overwrite what the family already typed.
                if (person.BirthYear is null) (person.BirthYear, person.BirthMonth, person.BirthDay, person.BirthCirca) = (p.Birth.Year, p.Birth.Month, p.Birth.Day, p.Birth.Circa);
                person.BirthPlace ??= p.BirthPlace;
                person.BirthSurname ??= p.BirthSurname;
                person.Nickname ??= p.Nickname;
                person.Bio ??= p.Bio;
                if (person.Sex == Sex.Unknown) person.Sex = p.Sex;
                if (!p.IsLiving && person.IsLiving)
                {
                    person.IsLiving = false;
                    (person.DeathYear, person.DeathMonth, person.DeathDay, person.DeathCirca) = (p.Death.Year, p.Death.Month, p.Death.Day, p.Death.Circa);
                    person.DeathPlace ??= p.DeathPlace;
                }
                person.UpdatedAt = clock.GetUtcNow().UtcDateTime;
                await db.SaveChangesAsync();
                matched++;
            }
            else
            {
                person = await people.CreateAsync(new PersonInput(p.Given, p.Surname, p.BirthSurname, p.Nickname, p.Sex,
                    p.Birth, p.BirthPlace, p.IsLiving, p.Death, p.DeathPlace, p.Bio, null, null));
                created++;
            }
            byLine[p.Line] = person.Id;

            if (row.Account is AccountPlan.Create && p.Email is { } email)
            {
                var storyteller = p.Roles.Contains(Roles.Storyteller);
                var made = await accounts.InviteAsync(new UserInput(p.DisplayName, email, p.Roles, storyteller ? coreSet : null, Pacing.Weekly),
                    sendInvitations && p.IsLiving, inviterName);
                if (made.Succeeded && made.UserId is { } userId)
                {
                    accountsCreated++;
                    if (sendInvitations && p.IsLiving) invitations++;
                    await LinkAsync(person.Id, userId);
                }
                else warnings.Add($"Line {p.Line}: no account was made for {p.DisplayName}: {made.Error}");
            }
            else if (row.Account is AccountPlan.LinkExisting && p.Email is { } existingEmail)
            {
                var userId = await db.Users.Where(u => u.NormalizedEmail == existingEmail.ToUpperInvariant()).Select(u => u.Id).SingleAsync();
                if (await LinkAsync(person.Id, userId)) accountsLinked++;
                else warnings.Add($"Line {p.Line}: {p.DisplayName} already has a different account, so {existingEmail} wasn't linked.");
            }
            else if (row.Account is AccountPlan.Problem)
            {
                warnings.Add($"Line {p.Line}: {row.AccountText}");
            }
        }

        // Relationships, once everyone is in. Names point at other rows first, then at people already in the tree.
        var inFile = preview.Rows.Where(r => byLine.ContainsKey(r.Person.Line)).ToList();
        var tree = await db.People.AsNoTracking().ToListAsync();
        Guid? Resolve(string name)
        {
            var n = name.Trim();
            var rows = inFile.Where(r => string.Equals(r.Person.DisplayName, n, StringComparison.OrdinalIgnoreCase)
                                      || string.Equals(r.Person.Nickname, n, StringComparison.OrdinalIgnoreCase)).ToList();
            if (rows.Count == 1) return byLine[rows[0].Person.Line];
            if (rows.Count > 1) return null;
            var known = tree.Where(t => string.Equals(t.DisplayName, n, StringComparison.OrdinalIgnoreCase)
                                     || string.Equals(t.Nickname, n, StringComparison.OrdinalIgnoreCase)).ToList();
            return known.Count == 1 ? known[0].Id : null;
        }

        foreach (var row in inFile)
        {
            var id = byLine[row.Person.Line];
            foreach (var name in row.Person.Parents)
            {
                if (Resolve(name) is not { } parent) { warnings.Add($"Line {row.Person.Line}: couldn't tell who \"{name}\" is, so they weren't added as {row.Person.DisplayName}'s parent."); continue; }
                if (await people.AddParentAsync(id, parent, ParentKind.Biological) is { } error) warnings.Add($"Line {row.Person.Line}: {error}");
                else links++;
            }
            foreach (var name in row.Person.Partners)
            {
                if (Resolve(name) is not { } partner) { warnings.Add($"Line {row.Person.Line}: couldn't tell who \"{name}\" is, so they weren't added as {row.Person.DisplayName}'s partner."); continue; }
                var already = await db.Partnerships.AnyAsync(x => (x.Person1Id == id && x.Person2Id == partner) || (x.Person1Id == partner && x.Person2Id == id));
                if (already) continue;   // both rows name each other, or it was in the tree already
                if (await people.AddPartnerAsync(id, partner, PartnershipKind.Married, null, null) is { } error) warnings.Add($"Line {row.Person.Line}: {error}");
                else links++;
            }
        }

        audit.Add("people.import", $"Imported people from a spreadsheet: {created} new, {matched} matched, {skipped} skipped, {accountsCreated} accounts, {links} relationships");
        await db.SaveChangesAsync();
        try { File.Delete(path); } catch (IOException) { }
        return new PeopleImportResult(created, matched, skipped, accountsCreated, accountsLinked, invitations, links, warnings);
    }

    /// <summary>Links a tree person to a sign-in account, unless either is already linked to someone else.</summary>
    private async Task<bool> LinkAsync(Guid personId, Guid userId)
    {
        var person = await db.People.SingleAsync(p => p.Id == personId);
        if (person.LinkedUserId == userId) return true;
        if (person.LinkedUserId is not null || await db.People.AnyAsync(p => p.LinkedUserId == userId)) return false;
        person.LinkedUserId = userId;
        await db.SaveChangesAsync();
        return true;
    }
}
