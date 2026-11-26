using System.Globalization;
using System.Net;
using System.Text;
using FamilyMemories.Data;
using FamilyMemories.Family;

namespace FamilyMemories.Exports;

/// <summary>
/// A simple generational family-tree drawing as SVG (for the PDF, the book and the static website):
/// one row per generation, couples side by side, lines from parents to children.
/// </summary>
public static class TreeSvg
{
    private const int BoxW = 190, BoxH = 58, GapX = 24, RowH = 118, Pad = 20;

    public static string Render(IReadOnlyList<Person> people, IReadOnlyList<ParentChild> parents, IReadOnlyList<Partnership> partners)
    {
        if (people.Count == 0) return "";
        var ids = people.Select(p => p.Id).ToHashSet();
        var parentsOf = parents.Where(x => ids.Contains(x.ParentId) && ids.Contains(x.ChildId))
            .GroupBy(x => x.ChildId).ToDictionary(g => g.Key, g => g.Select(x => x.ParentId).ToList());

        // Generation = longest chain of parents above (guarded against bad data).
        var gen = new Dictionary<Guid, int>();
        int Gen(Guid id, int depth = 0)
        {
            if (gen.TryGetValue(id, out var g)) return g;
            if (depth > 50) return 0;
            g = parentsOf.TryGetValue(id, out var ps) ? ps.Max(p => Gen(p, depth + 1)) + 1 : 0;
            return gen[id] = g;
        }
        foreach (var p in people) Gen(p.Id);
        // Partners share a row.
        for (var pass = 0; pass < 3; pass++)
            foreach (var link in partners.Where(l => ids.Contains(l.Person1Id) && ids.Contains(l.Person2Id)))
            {
                var g = Math.Max(gen[link.Person1Id], gen[link.Person2Id]);
                gen[link.Person1Id] = gen[link.Person2Id] = g;
            }

        // Order each row: by the average position of parents (so children sit under them), partners adjacent.
        var x = new Dictionary<Guid, double>();
        var rows = people.GroupBy(p => gen[p.Id]).OrderBy(g => g.Key).ToList();
        foreach (var row in rows)
        {
            var ordered = row.OrderBy(p => parentsOf.TryGetValue(p.Id, out var ps) && ps.All(x.ContainsKey) ? ps.Average(pp => x[pp]) : double.MaxValue)
                .ThenBy(p => p.BirthYear ?? 9999).ThenBy(p => p.GivenNames).ToList();
            var placed = new List<Person>();
            foreach (var p in ordered)
            {
                if (placed.Contains(p)) continue;
                placed.Add(p);
                foreach (var link in partners.Where(l => l.Person1Id == p.Id || l.Person2Id == p.Id))
                {
                    var other = ordered.FirstOrDefault(o => o.Id == (link.Person1Id == p.Id ? link.Person2Id : link.Person1Id));
                    if (other is not null && !placed.Contains(other)) placed.Add(other);
                }
            }
            for (var i = 0; i < placed.Count; i++) x[placed[i].Id] = Pad + i * (BoxW + GapX);
        }

        var width = (int)(x.Values.Max() + BoxW + Pad);
        var height = rows.Count * RowH + Pad;
        double Y(Guid id) => Pad + gen[id] * RowH;
        var sb = new StringBuilder();
        sb.Append(CultureInfo.InvariantCulture, $"""<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 {width} {height}" width="100%" role="img" aria-label="Family tree" class="family-tree-svg">""");
        sb.Append("<g fill=\"none\" stroke=\"#6b6255\" stroke-width=\"2\">");
        foreach (var link in partners.Where(l => x.ContainsKey(l.Person1Id) && x.ContainsKey(l.Person2Id)))
        {
            var (a, b) = x[link.Person1Id] < x[link.Person2Id] ? (link.Person1Id, link.Person2Id) : (link.Person2Id, link.Person1Id);
            sb.Append(CultureInfo.InvariantCulture, $"""<line x1="{x[a] + BoxW}" y1="{Y(a) + BoxH / 2}" x2="{x[b]}" y2="{Y(b) + BoxH / 2}"{(link.Kind == PartnershipKind.Divorced ? " stroke-dasharray=\"6 4\"" : "")}/>""");
        }
        foreach (var (child, ps) in parentsOf.Where(kv => x.ContainsKey(kv.Key)))
        {
            var known = ps.Where(x.ContainsKey).ToList();
            if (known.Count == 0) continue;
            var fromX = known.Average(p => x[p] + BoxW / 2.0);
            var fromY = known.Max(p => Y(p)) + BoxH;
            var toX = x[child] + BoxW / 2.0;
            var toY = Y(child);
            var midY = toY - (RowH - BoxH) / 2.0;
            sb.Append(CultureInfo.InvariantCulture, $"""<path d="M{fromX:0.#} {fromY} V{midY:0.#} H{toX:0.#} V{toY}"/>""");
        }
        sb.Append("</g>");
        foreach (var p in people)
        {
            var span = PeopleService.LifeSpan(p, isAdmin: false);
            sb.Append(CultureInfo.InvariantCulture, $"""<g transform="translate({x[p.Id]:0.#} {Y(p.Id):0.#})"><rect width="{BoxW}" height="{BoxH}" rx="8" fill="#fffdf8" stroke="#1f1b16" stroke-width="1.5"/>""");
            sb.Append(CultureInfo.InvariantCulture, $"""<text x="{BoxW / 2}" y="24" text-anchor="middle" font-size="15" font-weight="700" fill="#1f1b16">{Esc(Trim(p.DisplayName, 24))}</text>""");
            if (span.Length > 0)
                sb.Append(CultureInfo.InvariantCulture, $"""<text x="{BoxW / 2}" y="44" text-anchor="middle" font-size="12.5" fill="#4a4238">{Esc(span)}</text>""");
            sb.Append("</g>");
        }
        sb.Append("</svg>");
        return sb.ToString();
    }

    private static string Trim(string s, int max) => s.Length <= max ? s : s[..(max - 1)] + "…";
    private static string Esc(string s) => WebUtility.HtmlEncode(s);
}
