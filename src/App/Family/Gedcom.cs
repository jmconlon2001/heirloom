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

public sealed record GedcomIndividual(
    string Xref, string Given, string Surname, string? Nickname, Sex Sex,
    FuzzyDate Birth, string? BirthPlace, bool Deceased, FuzzyDate Death, string? DeathPlace, string? Note)
{
    public string DisplayName => string.Join(' ', new[] { Given, Surname }.Where(s => s.Length > 0)) is { Length: > 0 } n ? n : "Unnamed";
}

public sealed record GedcomFamily(string Xref, string? Husband, string? Wife, IReadOnlyList<string> Children, int? MarriageYear, bool Divorced);

public sealed record GedcomData(IReadOnlyList<GedcomIndividual> Individuals, IReadOnlyList<GedcomFamily> Families);

/// <summary>
/// GEDCOM 5.5.1 reading and writing: individuals, families, names, dates and places.
/// Sources, citations and notes on events are deliberately ignored (§7).
/// </summary>
public static class Gedcom
{
    private static readonly string[] Months = ["JAN", "FEB", "MAR", "APR", "MAY", "JUN", "JUL", "AUG", "SEP", "OCT", "NOV", "DEC"];

    // ------------------------------------------------------------------ reading

    public static List<GedcomNode> ParseNodes(string text)
    {
        var roots = new List<GedcomNode>();
        var stack = new Stack<GedcomNode>();
        foreach (var raw in text.TrimStart('﻿').Split('\n'))
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

    public static GedcomData Parse(string text)
    {
        var nodes = ParseNodes(text);
        var individuals = nodes.Where(n => n.Tag == "INDI" && n.Xref is not null).Select(ReadIndividual).ToList();
        var families = nodes.Where(n => n.Tag == "FAM" && n.Xref is not null).Select(f => new GedcomFamily(
            f.Xref!,
            f.Child("HUSB")?.Value.Trim(),
            f.Child("WIFE")?.Value.Trim(),
            [.. f.All("CHIL").Select(c => c.Value.Trim())],
            ParseDate(f.Child("MARR")?.Child("DATE")?.Value).Year,
            f.Child("DIV") is not null)).ToList();
        return new GedcomData(individuals, families);
    }

    private static GedcomIndividual ReadIndividual(GedcomNode n)
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
        return new GedcomIndividual(n.Xref!, given, surname, string.IsNullOrEmpty(nick) ? null : nick, sex,
            ParseDate(birt?.Child("DATE")?.Value), Place(birt), deceased,
            ParseDate(deat?.Child("DATE")?.Value), Place(deat),
            n.Child("NOTE")?.Value is { Length: > 0 and < 4000 } note && !note.StartsWith('@') ? note : null);
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
    internal static FuzzyDate ParseDate(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return default;
        var tokens = value.ToUpperInvariant().Replace("(", " ").Replace(")", " ").Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();
        var circa = false;
        if (tokens.Count > 0 && tokens[0] is "ABT" or "ABOUT" or "CIRCA" or "CA" or "C." or "EST" or "CAL" or "BEF" or "AFT" or "BET" or "FROM" or "TO" or "INT")
        {
            circa = tokens[0] is not "FROM" and not "TO" and not "INT";
            tokens.RemoveAt(0);
        }
        var cut = tokens.FindIndex(t => t is "AND" or "TO");
        if (cut >= 0) { tokens = tokens[..cut]; circa = true; }

        int? day = null, month = null, year = null;
        foreach (var t in tokens)
        {
            var m = Array.IndexOf(Months, t);
            if (m >= 0) month = m + 1;
            else if (int.TryParse(t.Split('/')[0], out var n)) { if (n > 31) year = n; else day = n; }
        }
        if (month is null) day = null;
        return FuzzyDate.Normalize(year, month, day, circa, null, null, DateTime.UtcNow.Year);
    }

    // ------------------------------------------------------------------ writing

    public static string Export(IReadOnlyList<Person> people, IReadOnlyList<ParentChild> parents, IReadOnlyList<Partnership> partnerships, string appName)
    {
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

        foreach (var p in people)
        {
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
            if (p.BirthYear is not null || p.BirthPlace is not null)
            {
                Line(1, "BIRT");
                if (FormatDate(p.Birth) is { } bd) Line(2, "DATE", bd);
                if (p.BirthPlace is not null) Line(2, "PLAC", p.BirthPlace);
            }
            if (!p.IsLiving)
            {
                if (p.DeathYear is null && p.DeathPlace is null) Line(1, "DEAT", "Y");
                else
                {
                    Line(1, "DEAT");
                    if (FormatDate(p.Death) is { } dd) Line(2, "DATE", dd);
                    if (p.DeathPlace is not null) Line(2, "PLAC", p.DeathPlace);
                }
            }
            if (p.Bio is not null) Line(1, "NOTE", p.Bio);
            foreach (var f in families.Where(f => f.Children.Contains(p.Id))) Line(1, "FAMC", f.Xref);
            foreach (var f in families.Where(f => f.Husband == p.Id || f.Wife == p.Id)) Line(1, "FAMS", f.Xref);
        }

        foreach (var f in families)
        {
            Line(0, "FAM", xref: f.Xref);
            if (f.Husband is { } h) Line(1, "HUSB", ids[h]);
            if (f.Wife is { } w) Line(1, "WIFE", ids[w]);
            if (f.Partnership is { } link)
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
        if (d.Year is not { } y) return null;
        var text = (d.Month, d.Day) switch
        {
            ({ } m, { } day) => $"{day} {Months[m - 1]} {y}",
            ({ } m, null) => $"{Months[m - 1]} {y}",
            _ => y.ToString(CultureInfo.InvariantCulture),
        };
        return d.Circa ? "ABT " + text : text;
    }
}
