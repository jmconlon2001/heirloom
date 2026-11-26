using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using FamilyMemories.Data;

namespace FamilyMemories.Imports;

/// <summary>One data row of a spreadsheet: its line number in the file (for messages) and its cells by column key.</summary>
public sealed record CsvRow(int Line, IReadOnlyDictionary<string, string> Cells)
{
    public string? Get(string key) => Cells.TryGetValue(key, out var v) && !string.IsNullOrWhiteSpace(v) ? v.Trim() : null;
}

/// <summary>
/// Reads spreadsheets saved as CSV (Excel, Numbers, Google Sheets, LibreOffice): quoted cells, line breaks
/// inside a cell, a UTF-8 byte-order mark, and comma, semicolon or tab separators (European Excel uses semicolons).
/// </summary>
public static partial class Csv
{
    /// <summary>Reads the file into header-keyed rows. Headers are matched loosely: "First name", "first_name" and "FirstName" are the same.</summary>
    /// <param name="aliases">Normalized header → column key, e.g. "firstname" → "given".</param>
    public static (List<CsvRow> Rows, List<string> UnknownColumns) Read(string text, IReadOnlyDictionary<string, string> aliases)
    {
        var records = Parse(text);
        if (records.Count == 0) return ([], []);
        var header = records[0].Cells;
        var keys = header.Select(h => aliases.GetValueOrDefault(NormalizeHeader(h))).ToList();
        var unknown = header.Where((h, i) => keys[i] is null && h.Trim().Length > 0).Select(h => h.Trim()).ToList();

        var rows = new List<CsvRow>();
        foreach (var (line, cells) in records.Skip(1))
        {
            if (cells.All(string.IsNullOrWhiteSpace)) continue;
            var map = new Dictionary<string, string>();
            for (var i = 0; i < cells.Count && i < keys.Count; i++)
            {
                if (keys[i] is not { } key || string.IsNullOrWhiteSpace(cells[i])) continue;
                // Two columns for the same thing (Mother and Father both mean "parents"): keep both.
                map[key] = map.TryGetValue(key, out var existing) ? $"{existing}; {cells[i].Trim()}" : cells[i].Trim();
            }
            rows.Add(new CsvRow(line, map));
        }
        return (rows, unknown);
    }

    public static string NormalizeHeader(string header) => NonLetters().Replace(header.ToLowerInvariant(), "");

    /// <summary>RFC 4180 parsing. Returns each record with the line number it started on.</summary>
    internal static List<(int Line, List<string> Cells)> Parse(string text)
    {
        if (text.Length > 0 && text[0] == '﻿') text = text[1..];
        var separator = DetectSeparator(text);
        var records = new List<(int, List<string>)>();
        var cells = new List<string>();
        var cell = new StringBuilder();
        var inQuotes = false;
        var line = 1;
        var recordLine = 1;

        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (inQuotes)
            {
                if (c == '"' && i + 1 < text.Length && text[i + 1] == '"') { cell.Append('"'); i++; }
                else if (c == '"') inQuotes = false;
                else
                {
                    if (c == '\n') line++;
                    if (c != '\r') cell.Append(c);
                }
                continue;
            }
            if (c == '"' && cell.Length == 0) inQuotes = true;
            else if (c == separator) { cells.Add(cell.ToString()); cell.Clear(); }
            else if (c is '\n' or '\r')
            {
                if (c == '\r' && i + 1 < text.Length && text[i + 1] == '\n') i++;
                cells.Add(cell.ToString()); cell.Clear();
                records.Add((recordLine, cells));
                cells = [];
                line++;
                recordLine = line;
            }
            else cell.Append(c);
        }
        if (cell.Length > 0 || cells.Count > 0)
        {
            cells.Add(cell.ToString());
            records.Add((recordLine, cells));
        }
        return records;
    }

    private static char DetectSeparator(string text)
    {
        var end = text.IndexOf('\n');
        var first = end < 0 ? text : text[..end];
        var counts = new[] { ',', ';', '\t' }.Select(s => (s, n: first.Count(ch => ch == s))).OrderByDescending(x => x.n).First();
        return counts.n == 0 ? ',' : counts.s;
    }

    /// <summary>Writes one CSV line, quoting where needed.</summary>
    public static string Line(params string[] cells) =>
        string.Join(',', cells.Select(c => c.IndexOfAny([',', '"', '\n', '\r']) >= 0 ? $"\"{c.Replace("\"", "\"\"")}\"" : c)) + "\r\n";

    /// <summary>Several names in one cell: "Joe Smith; Rose Smith" (commas too, when there's no semicolon).</summary>
    public static List<string> Names(string? cell)
    {
        if (string.IsNullOrWhiteSpace(cell)) return [];
        var parts = cell.Contains(';') ? cell.Split(';') : cell.Contains('|') ? cell.Split('|') : cell.Split(',');
        return [.. parts.Select(p => p.Trim()).Where(p => p.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase)];
    }

    public static bool? YesNo(string? cell) => cell?.Trim().ToLowerInvariant() switch
    {
        null or "" => null,
        "yes" or "y" or "true" or "1" or "x" or "living" or "alive" => true,
        "no" or "n" or "false" or "0" or "deceased" or "dead" => false,
        _ => null,
    };

    [GeneratedRegex("[^a-z0-9]")]
    private static partial Regex NonLetters();
}

/// <summary>Dates the way people type them in a spreadsheet.</summary>
public static partial class DateText
{
    private static readonly CultureInfo En = CultureInfo.GetCultureInfo("en-US");

    /// <summary>
    /// "1965", "about 1965", "c. 1965", "1970s", "June 1972", "June 3, 1972", "3 June 1972", "1972-06-03", "6/3/1972" (US order,
    /// unless the first number can only be a day), and Excel serial numbers. Anything else becomes the date's words (Label) and
    /// <c>Understood</c> is false.
    /// </summary>
    public static (FuzzyDate Date, bool Understood) Parse(string? text, int currentYear)
    {
        if (string.IsNullOrWhiteSpace(text)) return (FuzzyDate.Empty, true);
        var s = text.Trim();
        var circa = false;
        var m = CircaPrefix().Match(s);
        if (m.Success) { circa = true; s = s[m.Length..].Trim(); }

        FuzzyDate? found = null;
        if (Decade().Match(s) is { Success: true } d) found = new(null, null, null, circa, int.Parse(d.Groups[1].Value, En), null);
        else if (Iso().Match(s) is { Success: true } iso)
            found = new(Int(iso.Groups[1]), Int(iso.Groups[2]), Int(iso.Groups[3]), circa, null, null);
        else if (Slashes().Match(s) is { Success: true } sl)
        {
            int a = Int(sl.Groups[1])!.Value, b = Int(sl.Groups[2])!.Value, y = Int(sl.Groups[3])!.Value;
            if (y < 100) y += y > currentYear % 100 ? 1900 : 2000;
            var (month, day) = a > 12 ? (b, a) : (a, b);
            found = new(y, month, day, circa, null, null);
        }
        else if (DateTime.TryParseExact(s, ["MMMM d, yyyy", "MMM d, yyyy", "MMMM d yyyy", "MMM d yyyy", "d MMMM yyyy", "d MMM yyyy", "MMMM yyyy", "MMM yyyy", "MMM. yyyy", "MMM. d, yyyy"],
                     En, DateTimeStyles.AllowWhiteSpaces, out var dt))
        {
            var hasDay = DayNumber().IsMatch(s.Replace(dt.Year.ToString(En), ""));
            found = new(dt.Year, dt.Month, hasDay ? dt.Day : null, circa, null, null);
        }
        else if (int.TryParse(s, NumberStyles.None, En, out var serial) && serial is > 2000 and < 80000 && s.Length == 5)
        {
            var date = new DateTime(1899, 12, 30).AddDays(serial);   // Excel stores dates as days since 1899-12-30
            found = new(date.Year, date.Month, date.Day, circa, null, null);
        }

        if (found is { } f)
        {
            var clean = FuzzyDate.Normalize(f.Year, f.Month, f.Day, f.Circa, f.Decade, null, currentYear);
            if (clean.HasDate) return (clean, true);
        }
        return (FuzzyDate.Normalize(null, null, null, false, null, text.Trim(), currentYear), false);
    }

    private static int? Int(Group g) => g.Success && g.Value.Length > 0 ? int.Parse(g.Value, En) : null;

    [GeneratedRegex(@"^(about|around|abt\.?|approx\.?|approximately|circa|ca\.?|c\.|c|~)\s*", RegexOptions.IgnoreCase)]
    private static partial Regex CircaPrefix();
    [GeneratedRegex(@"^(?:the\s+)?(\d{3}0)'?s$", RegexOptions.IgnoreCase)]
    private static partial Regex Decade();
    [GeneratedRegex(@"^(\d{4})(?:-(\d{1,2})(?:-(\d{1,2}))?)?$")]
    private static partial Regex Iso();
    [GeneratedRegex(@"^(\d{1,2})[/.](\d{1,2})[/.](\d{2}|\d{4})$")]
    private static partial Regex Slashes();
    [GeneratedRegex(@"\b\d{1,2}\b")]
    private static partial Regex DayNumber();
}
