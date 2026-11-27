using System.Globalization;
using System.Text;
using FamilyMemories.Data;

namespace FamilyMemories.Family;

/// <summary>One line of a GEDCOM file and the lines nested under it.</summary>
public sealed class GedcomNode
{
    public int Level { get; init; }
    public string? Xref { get; init; }
    public string Tag { get; init; } = "";
    public string Value { get; set; } = "";
    public List<GedcomNode> Children { get; } = [];

    public GedcomNode? Child(string tag) => Children.FirstOrDefault(c => c.Tag == tag);
    public IEnumerable<GedcomNode> All(string tag) => Children.Where(c => c.Tag == tag);
}

public sealed record GedcomFact(string Kind, string Label, FuzzyDate Date, string? Place, string? Value, string? Note, IReadOnlyList<FactCitation> Citations);

public sealed record GedcomLink(string Label, string Url);

public sealed record GedcomIndividual(
    string Xref, string Given, string Surname, string? Nickname, Sex Sex,
    FuzzyDate Birth, string? BirthPlace, bool Deceased, FuzzyDate Death, string? DeathPlace, string? Note)
{
    /// <summary>Life events with their places, notes and sources, in file order (birth and death included, for their sources).</summary>
    public IReadOnlyList<GedcomFact> Facts { get; init; } = [];
    public IReadOnlyList<GedcomLink> Links { get; init; } = [];

    public string DisplayName => string.Join(' ', new[] { Given, Surname }.Where(s => s.Length > 0)) is { Length: > 0 } n ? n : "Unnamed";
}

public sealed record GedcomFamily(string Xref, string? Husband, string? Wife, IReadOnlyList<string> Children, int? MarriageYear, bool Divorced)
{
    /// <summary>The couple's events (marriage, divorce…), shown on both partners.</summary>
    public IReadOnlyList<GedcomFact> Facts { get; init; } = [];
}

public sealed record GedcomData(IReadOnlyList<GedcomIndividual> Individuals, IReadOnlyList<GedcomFamily> Families);

/// <summary>
/// GEDCOM 5.5.1 reading and writing: individuals, families, names, dates, places, life events with their notes and source
/// citations, and links. Heirloom shows these but isn't a genealogy editor: the family's main program owns them (D-076).
/// </summary>
public static class Gedcom
{
    private static readonly string[] Months = ["JAN", "FEB", "MAR", "APR", "MAY", "JUN", "JUL", "AUG", "SEP", "OCT", "NOV", "DEC"];

    // ------------------------------------------------------------------ reading

    public static List<GedcomNode> ParseNodes(string text)
    {
        var roots = new List<GedcomNode>();
        var stack = new Stack<GedcomNode>();
        // Reunion and other classic Mac software end lines with a bare CR; Windows with CRLF; everyone else LF.
        foreach (var raw in text.TrimStart('﻿').Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
        {
            var line = raw.TrimEnd('\r').TrimStart();
            if (line.Length == 0) continue;
            var parts = line.Split(' ', 3);
            if (!int.TryParse(parts[0], out var level) || parts.Length < 2) continue;

            string? xref = null;
            string tag, value;
            if (parts[1].StartsWith('@') && parts.Length >= 3)
            {
                xref = parts[1];
                var rest = parts[2].Split(' ', 2);
                tag = rest[0];
                value = rest.Length > 1 ? rest[1] : "";
            }
            else
            {
                tag = parts[1];
                value = parts.Length > 2 ? parts[2] : "";
            }

            while (stack.Count > 0 && stack.Peek().Level >= level) stack.Pop();
            if (tag is "CONC" or "CONT" && stack.Count > 0)
            {
                stack.Peek().Value += (tag == "CONT" ? "\n" : "") + value;
                continue;
            }

            var node = new GedcomNode { Level = level, Xref = xref, Tag = tag.ToUpperInvariant(), Value = value };
            if (stack.Count == 0) roots.Add(node); else stack.Peek().Children.Add(node);
            stack.Push(node);
        }
        return roots;
    }

    /// <summary>Events and facts we show, with what people read. Anything else under EVEN/FACT uses its TYPE.</summary>
    internal static readonly IReadOnlyDictionary<string, string> EventLabels = new Dictionary<string, string>
    {
        ["BIRT"] = "Birth", ["CHR"] = "Christening", ["BAPM"] = "Baptism", ["CONF"] = "Confirmation", ["BARM"] = "Bar mitzvah",
        ["BASM"] = "Bat mitzvah", ["FCOM"] = "First communion", ["ADOP"] = "Adoption", ["GRAD"] = "Graduation", ["EDUC"] = "Education",
        ["OCCU"] = "Occupation", ["RESI"] = "Residence", ["CENS"] = "Census", ["EMIG"] = "Emigration", ["IMMI"] = "Immigration",
        ["NATU"] = "Naturalization", ["MILI"] = "Military service", ["_MILT"] = "Military service", ["_MILI"] = "Military service",
        ["RELI"] = "Religion", ["ORDN"] = "Ordination", ["RETI"] = "Retirement", ["PROP"] = "Property", ["WILL"] = "Will",
        ["PROB"] = "Probate", ["DEAT"] = "Death", ["BURI"] = "Burial", ["CREM"] = "Cremation", ["TITL"] = "Title",
        ["NATI"] = "Nationality", ["DSCR"] = "Description",
        ["MARR"] = "Marriage", ["MARB"] = "Marriage banns", ["MARL"] = "Marriage license", ["ENGA"] = "Engagement",
        ["DIV"] = "Divorce", ["ANUL"] = "Annulment", ["SEPA"] = "Separation", ["EVEN"] = "Event", ["FACT"] = "Fact",
        // Written by Reunion and other programs, though not in the 5.5.1 list. (RACE and SSN are deliberately not read.)
        ["CITN"] = "Citizenship", ["EMPL"] = "Employment", ["HIST"] = "History", ["NAMR"] = "Religious name",
        ["CAUS"] = "Cause of death", ["ALIA"] = "Also known as", ["HAIR"] = "Hair", ["HEIG"] = "Height", ["CLER"] = "Clergy",
        ["WEIG"] = "Weight", ["EYES"] = "Eyes", ["MEDI"] = "Medical",
    };

    public static GedcomData Parse(string text)
    {
        var nodes = ParseNodes(text);
        var sources = nodes.Where(n => n.Tag == "SOUR" && n.Xref is not null).ToDictionary(n => n.Xref!);
        var notes = nodes.Where(n => n.Tag == "NOTE" && n.Xref is not null).ToDictionary(n => n.Xref!, n => n.Value);
        var context = new ReadContext(sources, notes);
        var individuals = nodes.Where(n => n.Tag == "INDI" && n.Xref is not null).Select(n => ReadIndividual(n, context)).ToList();
        var families = nodes.Where(n => n.Tag == "FAM" && n.Xref is not null).Select(f => new GedcomFamily(
            f.Xref!,
            f.Child("HUSB")?.Value.Trim(),
            f.Child("WIFE")?.Value.Trim(),
            [.. f.All("CHIL").Select(c => c.Value.Trim())],
            ParseDate(f.Child("MARR")?.Child("DATE")?.Value).Year,
            f.Child("DIV") is not null)
        {
            Facts = [.. f.Children.Where(c => c.Tag is "MARR" or "MARB" or "MARL" or "ENGA" or "DIV" or "ANUL" or "SEPA" or "EVEN").Select(c => ReadFact(c, context))],
        }).ToList();
        return new GedcomData(individuals, families);
    }

    private sealed record ReadContext(IReadOnlyDictionary<string, GedcomNode> Sources, IReadOnlyDictionary<string, string> Notes)
    {
        /// <summary>A NOTE's text, following a pointer to a shared NOTE record.</summary>
        public string? Note(GedcomNode? node) => node is null ? null
            : node.Value.Trim() is var v && v.StartsWith('@') ? Notes.GetValueOrDefault(v)?.Trim() is { Length: > 0 } shared ? shared : null
            : v.Length > 0 ? v : null;

        /// <summary>A SOUR citation: a pointer to a source record (title, URL) with this citation's PAGE, or a source written inline.</summary>
        public FactCitation? Citation(GedcomNode sour)
        {
            var page = sour.Child("PAGE")?.Value.Trim() is { Length: > 0 } pg ? pg : null;
            var value = sour.Value.Trim();
            if (value.StartsWith('@'))
            {
                if (!Sources.TryGetValue(value, out var record)) return page is null ? null : new FactCitation("Source", page);
                var title = record.Child("TITL")?.Value.Trim() is { Length: > 0 } t ? t
                    : record.Child("ABBR")?.Value.Trim() is { Length: > 0 } a ? a
                    : DescribeUntitled(record) ?? "Source";
                var url = new[] { "WWW", "_URL", "URL", "LOCA" }.Select(tag => record.Child(tag)?.Value.Trim()).FirstOrDefault(IsWebLink);
                return new FactCitation(title, page, url);
            }
            return value.Length > 0 ? new FactCitation(value, page) : null;
        }
    }

    /// <summary>
    /// Reunion builds many sources from templates with no title: a type, an author, a periodical, a date, a page. Those make
    /// a title people can read ("Newspaper, The Boston Globe, 26 Mar 2011, page B8"), and keep different sources apart.
    /// </summary>
    private static string? DescribeUntitled(GedcomNode record)
    {
        string? V(string tag) => record.Child(tag)?.Value.Trim() is { Length: > 0 } v ? v : null;
        var attachedTitle = record.Child("OBJE")?.Child("TITL")?.Value.Trim();
        var what = V("PERI") ?? V("PUBL") ?? (string.IsNullOrEmpty(attachedTitle) ? null : attachedTitle);
        var parts = new[] { V("TYPE"), V("AUTH") is { } by ? $"by {by}" : null, what, V("DATE"), V("PAGE") is { } pg ? $"page {pg}" : null }
            .Where(x => !string.IsNullOrEmpty(x)).ToList();
        if (parts.Count == 0 && V("TEXT") is { } text)
        {
            var first = text.Split('\n')[0].Trim();
            parts.Add(first.Length > 80 ? first[..80].TrimEnd() + "…" : first);
        }
        return parts.Count == 0 ? null : string.Join(", ", parts);
    }

    internal static bool IsWebLink(string? url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var u) && (u.Scheme == Uri.UriSchemeHttps || u.Scheme == Uri.UriSchemeHttp);

    private static GedcomFact ReadFact(GedcomNode n, ReadContext context)
    {
        var type = n.Child("TYPE")?.Value.Trim();
        var label = n.Tag is "EVEN" or "FACT" && type is { Length: > 0 } ? type : EventLabels.GetValueOrDefault(n.Tag, n.Tag);
        var raw = n.Value.Trim();
        var value = raw is { Length: > 0 } v && v != "Y" && !v.StartsWith('@') ? v : null;
        if (value is null && n.Child("ADDR") is { } addr && addr.Value.Trim().Length > 0) value = addr.Value.Trim();
        var notes = new List<string>();
        // Reunion writes some events as a pointer to a shared note ("1 MILI @N225@", "1 HIST @N309@").
        if (raw.StartsWith('@') && context.Notes.ContainsKey(raw) && context.Note(n) is { } pointed) notes.Add(pointed);
        if (n.Child("CAUS")?.Value.Trim() is { Length: > 0 } cause) notes.Add($"Cause: {cause}");
        notes.AddRange(n.All("NOTE").Select(context.Note).OfType<string>());
        return new GedcomFact(n.Tag, label, ParseDate(n.Child("DATE")?.Value), Place(n), value,
            notes.Count == 0 ? null : string.Join("\n\n", notes),
            [.. n.All("SOUR").Select(context.Citation).OfType<FactCitation>()]);
    }

    private static GedcomIndividual ReadIndividual(GedcomNode n, ReadContext context)
    {
        var name = n.Child("NAME");
        var (given, surname) = SplitName(name?.Value ?? "");
        if (name?.Child("GIVN") is { } givn && givn.Value.Length > 0) given = givn.Value.Trim();
        if (name?.Child("SURN") is { } surn && surn.Value.Length > 0) surname = surn.Value.Trim();
        var nick = name?.Child("NICK")?.Value.Trim() ?? n.Child("NICK")?.Value.Trim();

        var sex = (n.Child("SEX")?.Value.Trim().ToUpperInvariant()) switch
        {
            "F" => Sex.Female,
            "M" => Sex.Male,
            "X" => Sex.Other,
            _ => Sex.Unknown,
        };
        var birt = n.Child("BIRT");
        var deat = n.Child("DEAT");
        var deceased = deat is not null || n.Child("BURI") is not null;
        var notes = n.All("NOTE").Select(context.Note).OfType<string>().ToList();
        var facts = n.Children.Where(c => EventLabels.ContainsKey(c.Tag)).Select(c => ReadFact(c, context)).ToList();
        // Sources for the person as a whole, and for their name.
        if (n.All("SOUR").Concat(n.All("NAME").SelectMany(x => x.All("SOUR"))).Select(context.Citation).OfType<FactCitation>().Distinct().ToList() is { Count: > 0 } general)
            facts.Add(new GedcomFact("SOUR", "Sources", default, null, null, null, general));

        var links = new List<GedcomLink>();
        foreach (var w in n.Children.Where(c => c.Tag is "WWW" or "_URL" or "URL"))
            if (IsWebLink(w.Value.Trim())) links.Add(new GedcomLink(LinkLabel(w.Value.Trim()), w.Value.Trim()));
        if (n.Child("_FSFTID")?.Value.Trim() is { Length: > 0 } fs && System.Text.RegularExpressions.Regex.IsMatch(fs, "^[A-Z0-9-]{4,12}$"))
            links.Add(new GedcomLink("FamilySearch", $"https://www.familysearch.org/tree/person/details/{fs}"));
        // Reunion puts the FamilySearch ID in REFN ("1 REFN LJLG-H2H"); other REFNs are the program's own numbers.
        foreach (var refn in n.All("REFN"))
            if (System.Text.RegularExpressions.Regex.IsMatch(refn.Value.Trim(), "^[A-Z0-9]{4}-[A-Z0-9]{3,4}$")
                && (refn.Child("TYPE") is null || refn.Child("TYPE")!.Value.Contains("FamilySearch", StringComparison.OrdinalIgnoreCase)))
                links.Add(new GedcomLink("FamilySearch", $"https://www.familysearch.org/tree/person/details/{refn.Value.Trim()}"));
        foreach (var obje in n.All("OBJE"))
            if (obje.Child("FILE")?.Value.Trim() is { } file && IsWebLink(file))
                links.Add(new GedcomLink(obje.Child("TITL")?.Value.Trim() is { Length: > 0 } t ? t : obje.Child("FILE")!.Child("TITL")?.Value.Trim() ?? LinkLabel(file), file));

        return new GedcomIndividual(n.Xref!, given, surname, string.IsNullOrEmpty(nick) ? null : nick, sex,
            ParseDate(birt?.Child("DATE")?.Value), Place(birt), deceased,
            ParseDate(deat?.Child("DATE")?.Value), Place(deat),
            notes.Count == 0 ? null : string.Join("\n\n", notes))
        {
            Facts = facts,
            Links = [.. links.DistinctBy(l => l.Url)],
        };
    }

    /// <summary>"conlon.org", "FamilySearch", "Find a Grave"… from a link's address.</summary>
    internal static string LinkLabel(string url)
    {
        var host = Uri.TryCreate(url, UriKind.Absolute, out var u) ? u.Host.ToLowerInvariant() : url;
        if (host.StartsWith("www.")) host = host[4..];
        return host switch
        {
            _ when host.EndsWith("familysearch.org") => "FamilySearch",
            _ when host.EndsWith("findagrave.com") => "Find a Grave",
            _ when host.EndsWith("ancestry.com") => "Ancestry",
            _ when host.EndsWith("wikitree.com") => "WikiTree",
            _ when host.EndsWith("billiongraves.com") => "BillionGraves",
            _ => host,
        };
    }

    private static string? Place(GedcomNode? evt) => evt?.Child("PLAC")?.Value.Trim() is { Length: > 0 } p ? p : null;

    /// <summary>"John Henry /Smith/" → ("John Henry", "Smith").</summary>
    internal static (string Given, string Surname) SplitName(string value)
    {
        var first = value.IndexOf('/');
        if (first < 0) return (value.Trim(), "");
        var second = value.IndexOf('/', first + 1);
        var surname = second < 0 ? value[(first + 1)..] : value[(first + 1)..second];
        var given = value[..first] + (second < 0 ? "" : " " + value[(second + 1)..]);
        return (System.Text.RegularExpressions.Regex.Replace(given, @"\s+", " ").Trim(), surname.Trim());
    }

    /// <summary>"12 JUN 1972", "JUN 1972", "ABT 1965", "BEF 1900", "BET 1940 AND 1945", "EST 1890" → a fuzzy date.</summary>
    private static readonly string[] MonthNames = ["January", "February", "March", "April", "May", "June", "July", "August", "September", "October", "November", "December"];

    /// <summary>
    /// "12 JUN 1972", "JUN 1972", "ABT 1965", "BEF 1900", "BET 1940 AND 1945", "bet 1885 1887", "1871/1874", "EST 1890" → a fuzzy
    /// date. A range or a choice keeps its first year and becomes "around". A day and month with no year ("7 SEP") is kept as words.
    /// </summary>
    internal static FuzzyDate ParseDate(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return default;
        var tokens = value.ToUpperInvariant().Replace("(", " ").Replace(")", " ").Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();
        var circa = false;
        if (tokens.Count > 0 && tokens[0].TrimEnd('.') is "ABT" or "ABOUT" or "CIRCA" or "CA" or "C" or "EST" or "CAL" or "BEF" or "BEFORE"
                or "AFT" or "AFTER" or "BET" or "BETW" or "BETWEEN" or "FROM" or "TO" or "INT")
        {
            circa = tokens[0] is not "FROM" and not "TO" and not "INT";
            tokens.RemoveAt(0);
        }
        var cut = tokens.FindIndex(t => t is "AND" or "TO" or "-");
        if (cut >= 0) { tokens = tokens[..cut]; circa = true; }

        int? day = null, month = null, year = null;
        foreach (var t in tokens)
        {
            var m = Array.IndexOf(Months, t.Length > 3 ? t[..3] : t);
            if (m >= 0 && month is null) month = m + 1;
            else if (int.TryParse(t.Split('/')[0], out var n))
            {
                if (n > 31) { if (year is null) { year = n; if (t.Contains('/')) circa = true; } else circa = true; }   // a second year: a range
                else if (day is null) day = n;
            }
        }
        if (month is null) day = null;
        // A date phrase in parentheses that isn't a date ("(the summer after the war)") is kept as words.
        var phrase = value.Trim() is ['(', .. var inner, ')'] ? inner.Trim() : null;
        if (year is null && month is null && phrase is { Length: > 0 }) return FuzzyDate.Normalize(null, null, null, false, null, phrase, DateTime.UtcNow.Year);
        if (year is null && month is { } onlyMonth)
            return FuzzyDate.Normalize(null, null, null, false, null, day is { } d ? $"{MonthNames[onlyMonth - 1]} {d}" : MonthNames[onlyMonth - 1], DateTime.UtcNow.Year);
        return FuzzyDate.Normalize(year, month, day, circa, null, null, DateTime.UtcNow.Year);
    }

    // ------------------------------------------------------------------ writing

    /// <summary>Tags that belong to a couple (written under FAM), not to one person.</summary>
    internal static readonly HashSet<string> FamilyEventTags = ["MARR", "MARB", "MARL", "ENGA", "DIV", "ANUL", "SEPA"];

    public static string Export(IReadOnlyList<Person> people, IReadOnlyList<ParentChild> parents, IReadOnlyList<Partnership> partnerships, string appName,
        IReadOnlyList<PersonFact>? facts = null, IReadOnlyList<PersonLink>? links = null)
    {
        var factsBy = (facts ?? []).GroupBy(f => f.PersonId).ToDictionary(g => g.Key, g => g.OrderBy(f => f.SortOrder).ToList());
        var linksBy = (links ?? []).GroupBy(l => l.PersonId).ToDictionary(g => g.Key, g => g.OrderBy(l => l.SortOrder).ToList());
        var sourceIds = new Dictionary<(string, string?), string>();
        string SourceId(FactCitation c)
        {
            if (!sourceIds.TryGetValue((c.Title, c.Url), out var id)) sourceIds[(c.Title, c.Url)] = id = $"@S{sourceIds.Count + 1}@";
            return id;
        }
        var sb = new StringBuilder();
        void Line(int level, string tag, string? value = null, string? xref = null)
        {
            var text = value ?? "";
            var lines = text.Split('\n');
            sb.Append(level).Append(' ');
            if (xref is not null) sb.Append(xref).Append(' ');
            sb.Append(tag);
            if (lines[0].Length > 0) sb.Append(' ').Append(lines[0]);
            sb.Append('\n');
            foreach (var more in lines.Skip(1)) sb.Append(level + 1).Append(" CONT ").Append(more).Append('\n');
        }

        Line(0, "HEAD");
        Line(1, "SOUR", "FAMILY_MEMORIES");
        Line(2, "NAME", appName);
        Line(1, "DATE", DateTime.UtcNow.ToString("d MMM yyyy", CultureInfo.InvariantCulture).ToUpperInvariant());
        Line(1, "GEDC");
        Line(2, "VERS", "5.5.1");
        Line(2, "FORM", "LINEAGE-LINKED");
        Line(1, "CHAR", "UTF-8");

        var ids = people.Select((p, i) => (p.Id, Xref: $"@I{i + 1}@")).ToDictionary(x => x.Id, x => x.Xref);
        var families = BuildFamilies(people, parents, partnerships);

        void Citations(int level, IEnumerable<FactCitation> citations)
        {
            foreach (var c in citations)
            {
                Line(level, "SOUR", SourceId(c));
                if (c.Page is not null) Line(level + 1, "PAGE", c.Page);
            }
        }
        void EventDetails(PersonFact f)
        {
            if (FormatDate(f.Date) is { } date) Line(2, "DATE", date);
            if (f.Place is not null) Line(2, "PLAC", f.Place);
            if (f.Note is not null) Line(2, "NOTE", f.Note);
            Citations(2, f.Citations);
        }

        foreach (var p in people)
        {
            var mine = factsBy.GetValueOrDefault(p.Id) ?? [];
            var birthFact = mine.FirstOrDefault(f => f.Kind == "BIRT");
            var deathFact = mine.FirstOrDefault(f => f.Kind == "DEAT");
            Line(0, "INDI", xref: ids[p.Id]);
            Line(1, "NAME", $"{p.GivenNames} /{p.Surname}/".Trim());
            if (p.GivenNames.Length > 0) Line(2, "GIVN", p.GivenNames);
            if (p.Surname.Length > 0) Line(2, "SURN", p.Surname);
            if (p.Nickname is not null) Line(2, "NICK", p.Nickname);
            if (p.BirthSurname is not null && p.BirthSurname != p.Surname)
            {
                Line(1, "NAME", $"{p.GivenNames} /{p.BirthSurname}/".Trim());
                Line(2, "TYPE", "birth");
            }
            Line(1, "SEX", p.Sex switch { Sex.Female => "F", Sex.Male => "M", Sex.Other => "X", _ => "U" });
            if (p.BirthYear is not null || p.BirthPlace is not null || birthFact is not null)
            {
                Line(1, "BIRT");
                if ((FormatDate(p.Birth) ?? (birthFact is null ? null : FormatDate(birthFact.Date))) is { } bd) Line(2, "DATE", bd);
                if (p.BirthPlace is not null) Line(2, "PLAC", p.BirthPlace);
                if (birthFact?.Note is { } bn) Line(2, "NOTE", bn);
                Citations(2, birthFact?.Citations ?? []);
            }
            if (!p.IsLiving)
            {
                // "Died, date unknown" — unless a burial or cremation already says so (the file had no DEAT then).
                if (p.DeathYear is null && p.DeathPlace is null && deathFact is null)
                {
                    if (!mine.Any(f => f.Kind is "BURI" or "CREM")) Line(1, "DEAT", "Y");
                }
                else
                {
                    Line(1, "DEAT");
                    if ((FormatDate(p.Death) ?? (deathFact is null ? null : FormatDate(deathFact.Date))) is { } dd) Line(2, "DATE", dd);
                    if (p.DeathPlace is not null) Line(2, "PLAC", p.DeathPlace);
                    if (deathFact?.Note is { } dn) Line(2, "NOTE", dn);
                    Citations(2, deathFact?.Citations ?? []);
                }
            }
            // A couple's events ("with …") go under FAM below; anything else stays on the person, as it came in.
            // Any further birth or death entries (a program may keep alternatives) go out as they came in.
            foreach (var extra in mine.Where(f => f.Kind == "BIRT" && f != birthFact).Concat(mine.Where(f => f.Kind == "DEAT" && f != deathFact)))
            {
                Line(1, extra.Kind, extra.Value);
                EventDetails(extra);
            }
            foreach (var f in mine.Where(f => f.Kind is not ("BIRT" or "DEAT" or "NOTE" or "SOUR") && !(FamilyEventTags.Contains(f.Kind) && (f.PartnerId is not null || (f.Value ?? "").StartsWith("with ")))))
            {
                var tag = EventLabels.ContainsKey(f.Kind) && f.Kind is not ("EVEN" or "FACT") ? f.Kind : "EVEN";
                Line(1, tag, f.Value);
                if (tag == "EVEN" || f.Kind is "FACT") Line(2, "TYPE", f.Label);
                EventDetails(f);
            }
            if (p.Bio is not null) Line(1, "NOTE", p.Bio);
            foreach (var n in mine.Where(f => f.Kind == "NOTE" && f.Note is not null)) Line(1, "NOTE", n.Note);
            foreach (var g in mine.Where(f => f.Kind == "SOUR")) Citations(1, g.Citations);
            foreach (var l in linksBy.GetValueOrDefault(p.Id) ?? [])
            {
                var fs = System.Text.RegularExpressions.Regex.Match(l.Url, @"^https://www\.familysearch\.org/tree/person/details/([A-Z0-9-]+)$");
                if (fs.Success) { Line(1, "_FSFTID", fs.Groups[1].Value); continue; }
                Line(1, "OBJE");
                Line(2, "FILE", l.Url);
                Line(3, "FORM", "URL");
                Line(2, "TITL", l.Label);
            }
            foreach (var f in families.Where(f => f.Children.Contains(p.Id))) Line(1, "FAMC", f.Xref);
            foreach (var f in families.Where(f => f.Husband == p.Id || f.Wife == p.Id)) Line(1, "FAMS", f.Xref);
        }

        foreach (var f in families)
        {
            Line(0, "FAM", xref: f.Xref);
            if (f.Husband is { } h) Line(1, "HUSB", ids[h]);
            if (f.Wife is { } w) Line(1, "WIFE", ids[w]);
            // The couple's own events, kept on each partner as "with …"; written once, from the first partner's copy.
            // Matched by the partner's id (two "(Living)" partners share a name); the "with …" text is a fallback for older rows.
            var coupleFacts = f.Husband is { } hh && f.Wife is { } ww
                ? (factsBy.GetValueOrDefault(hh) ?? []).Where(x => FamilyEventTags.Contains(x.Kind)
                    && (x.PartnerId == ww || (x.PartnerId is null && x.Value == $"with {people.First(q => q.Id == ww).DisplayName}"))).ToList()
                : [];
            foreach (var cf in coupleFacts)
            {
                Line(1, cf.Kind);
                EventDetails(cf);
            }
            if (f.Partnership is { } link && coupleFacts.Count == 0)
            {
                if (link.Kind != PartnershipKind.Partnered)
                {
                    Line(1, "MARR");
                    if (link.StartYear is { } y) Line(2, "DATE", y.ToString(CultureInfo.InvariantCulture));
                }
                if (link.Kind == PartnershipKind.Divorced)
                {
                    Line(1, "DIV");
                    if (link.EndYear is { } e) Line(2, "DATE", e.ToString(CultureInfo.InvariantCulture));
                }
            }
            foreach (var c in f.Children) Line(1, "CHIL", ids[c]);
        }
        foreach (var ((title, url), id) in sourceIds)
        {
            Line(0, "SOUR", xref: id);
            Line(1, "TITL", title);
            if (url is not null) Line(1, "WWW", url);
        }
        Line(0, "TRLR");
        return sb.ToString();
    }

    private sealed record Family(string Xref, Guid? Husband, Guid? Wife, Partnership? Partnership, List<Guid> Children);

    /// <summary>GEDCOM groups children under their parents' "family": one per couple, plus one per single or unpartnered parent set.</summary>
    private static List<Family> BuildFamilies(IReadOnlyList<Person> people, IReadOnlyList<ParentChild> parents, IReadOnlyList<Partnership> partnerships)
    {
        var sex = people.ToDictionary(p => p.Id, p => p.Sex);
        var result = new List<Family>();
        (Guid?, Guid?) Order(Guid a, Guid? b)
        {
            if (b is null) return sex.GetValueOrDefault(a) == Sex.Female ? (null, a) : (a, null);
            return sex.GetValueOrDefault(a) == Sex.Female && sex.GetValueOrDefault(b.Value) != Sex.Female ? (b, a) : (a, b);
        }

        foreach (var link in partnerships)
        {
            var (h, w) = Order(link.Person1Id, link.Person2Id);
            result.Add(new Family($"@F{result.Count + 1}@", h, w, link, []));
        }

        foreach (var group in parents.GroupBy(x => x.ChildId))
        {
            var ps = group.Select(x => x.ParentId).Distinct().Take(2).ToList();
            var family = result.FirstOrDefault(f =>
                ps.Count == 2 && new[] { f.Husband, f.Wife }.Where(x => x is not null).Select(x => x!.Value).ToHashSet().SetEquals(ps)
                || ps.Count == 1 && f.Partnership is null && (f.Husband ?? f.Wife) == ps[0] && (f.Husband is null || f.Wife is null));
            if (family is null)
            {
                var (h, w) = Order(ps[0], ps.Count > 1 ? ps[1] : null);
                family = new Family($"@F{result.Count + 1}@", h, w, null, []);
                result.Add(family);
            }
            family.Children.Add(group.Key);
        }
        return result;
    }

    internal static string? FormatDate(FuzzyDate d)
    {
        // A date kept as words ("September 7", "the summer after the war") goes out as a GEDCOM date phrase.
        if (d.Year is not { } y) return d.Label is { Length: > 0 } words ? $"({words})" : null;
        var text = (d.Month, d.Day) switch
        {
            ({ } m, { } day) => $"{day} {Months[m - 1]} {y}",
            ({ } m, null) => $"{Months[m - 1]} {y}",
            _ => y.ToString(CultureInfo.InvariantCulture),
        };
        return d.Circa ? "ABT " + text : text;
    }
}
