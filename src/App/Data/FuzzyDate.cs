using System.Globalization;

namespace FamilyMemories.Data;

/// <summary>
/// A date as people actually remember it: a year, maybe a month, maybe a day, maybe "around",
/// or just a decade, plus an optional phrase ("summer after the war").
/// </summary>
public readonly record struct FuzzyDate(int? Year, int? Month, int? Day, bool Circa, int? Decade, string? Label)
{
    public const int MinYear = 1800;
    private static readonly CultureInfo En = CultureInfo.GetCultureInfo("en-US");

    public static FuzzyDate Empty => default;

    public bool HasDate => Year is not null || Decade is not null;
    public bool IsEmpty => !HasDate && string.IsNullOrWhiteSpace(Label);

    /// <summary>
    /// Cleans user input: drops impossible parts instead of rejecting the whole thing,
    /// because a half-remembered date is still worth keeping.
    /// </summary>
    public static FuzzyDate Normalize(int? year, int? month, int? day, bool circa, int? decade, string? label, int currentYear)
    {
        if (year is { } y && (y < MinYear || y > currentYear + 1)) year = null;
        if (month is < 1 or > 12 || year is null) month = null;
        if (day is { } d && (month is null || d < 1 || d > DateTime.DaysInMonth(year!.Value, month.Value))) day = null;
        if (decade is { } dec && (dec % 10 != 0 || dec < MinYear || dec > currentYear)) decade = null;
        if (year is not null) decade = null; // the year is more precise
        label = string.IsNullOrWhiteSpace(label) ? null : label.Trim();
        if (label is { Length: > 200 }) label = label[..200];
        if (year is null && decade is null) circa = false;
        return new FuzzyDate(year, month, day, circa, decade, label);
    }

    /// <summary>"around 1965", "1970s", "June 1972", "June 3, 1972", "summer after the war (around 1945)".</summary>
    public string Format()
    {
        var date = FormatDate();
        if (Label is null) return date;
        return date.Length == 0 ? Label : $"{Label} ({date})";
    }

    private string FormatDate()
    {
        string core;
        if (Year is { } y)
        {
            core = (Month, Day) switch
            {
                ({ } m, { } d) => new DateTime(y, m, d).ToString("MMMM d, yyyy", En),
                ({ } m, null) => new DateTime(y, m, 1).ToString("MMMM yyyy", En),
                _ => y.ToString(En),
            };
        }
        else if (Decade is { } dec)
        {
            core = $"{dec}s";
        }
        else
        {
            return "";
        }
        return Circa ? $"around {core}" : core;
    }

    /// <summary>A sortable point in time for timelines (middle of whatever range is known).</summary>
    public DateOnly? SortDate => Year is { } y
        ? new DateOnly(y, Month ?? 7, Day ?? (Month is null ? 1 : 15))
        : Decade is { } dec ? new DateOnly(dec + 5, 1, 1) : null;

    public override string ToString() => Format();
}
