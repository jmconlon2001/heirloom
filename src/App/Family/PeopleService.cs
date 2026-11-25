using FamilyMemories.Admin;
using FamilyMemories.Data;
using Microsoft.EntityFrameworkCore;

namespace FamilyMemories.Family;

public sealed record Kin(Person Person, IReadOnlyList<Person> Parents, IReadOnlyList<(Person Partner, Partnership Link)> Partners,
    IReadOnlyList<Person> Children, IReadOnlyList<Person> Siblings);

public sealed record PersonInput(
    string GivenNames, string Surname, string? BirthSurname, string? Nickname, Sex Sex,
    FuzzyDate Birth, string? BirthPlace, bool IsLiving, FuzzyDate Death, string? DeathPlace,
    string? Bio, Guid? LinkedUserId, Guid? PhotoAttachmentId);

/// <summary>The family tree: people, parents and children, partners, and tagging people in stories and photos.</summary>
public sealed class PeopleService(AppDbContext db, AuditLog audit, TimeProvider clock)
{
    private DateTime Now => clock.GetUtcNow().UtcDateTime;

    public async Task<Person> CreateAsync(PersonInput input)
    {
        var person = new Person { Id = Guid.CreateVersion7(), CreatedAt = Now };
        Apply(person, input);
        db.People.Add(person);
        audit.Add("person.add", $"Added {person.DisplayName} to the family tree", "person", person.Id);
        await db.SaveChangesAsync();
        return person;
    }

    public async Task<bool> UpdateAsync(Guid id, PersonInput input)
    {
        var person = await db.People.SingleOrDefaultAsync(p => p.Id == id);
        if (person is null) return false;
        Apply(person, input);
        audit.Add("person.edit", $"Edited {person.DisplayName}", "person", person.Id);
        await db.SaveChangesAsync();
        return true;
    }

    private void Apply(Person p, PersonInput i)
    {
        p.GivenNames = Clean(i.GivenNames, 200) ?? "";
        p.Surname = Clean(i.Surname, 200) ?? "";
        p.BirthSurname = Clean(i.BirthSurname, 200);
        p.Nickname = Clean(i.Nickname, 100);
        p.Sex = i.Sex;
        (p.BirthYear, p.BirthMonth, p.BirthDay, p.BirthCirca) = (i.Birth.Year, i.Birth.Month, i.Birth.Day, i.Birth.Circa);
        p.BirthPlace = Clean(i.BirthPlace, 300);
        p.IsLiving = i.IsLiving;
        (p.DeathYear, p.DeathMonth, p.DeathDay, p.DeathCirca) = i.IsLiving ? (null, null, null, false) : (i.Death.Year, i.Death.Month, i.Death.Day, i.Death.Circa);
        p.DeathPlace = i.IsLiving ? null : Clean(i.DeathPlace, 300);
        p.Bio = Clean(i.Bio, 4000);
        p.LinkedUserId = i.LinkedUserId;
        p.PhotoAttachmentId = i.PhotoAttachmentId;
        p.UpdatedAt = Now;
    }

    public async Task DeleteAsync(Guid id)
    {
        var person = await db.People.SingleOrDefaultAsync(p => p.Id == id);
        if (person is null) return;
        await db.Memories.Where(m => m.AboutPersonId == id).ExecuteUpdateAsync(s => s.SetProperty(m => m.AboutPersonId, (Guid?)null));
        db.People.Remove(person);
        audit.Add("person.delete", $"Removed {person.DisplayName} from the family tree", "person", id);
        await db.SaveChangesAsync();
    }

    // ------------------------------------------------------------------ relationships

    public async Task<string?> AddParentAsync(Guid childId, Guid parentId, ParentKind kind)
    {
        if (childId == parentId) return "Someone can't be their own parent.";
        if (await IsDescendantAsync(parentId, of: childId)) return "That would make someone their own ancestor.";
        if (await db.ParentChildren.AnyAsync(x => x.ParentId == parentId && x.ChildId == childId)) return null;
        db.ParentChildren.Add(new ParentChild { ParentId = parentId, ChildId = childId, Kind = kind });
        audit.Add("person.relate", $"Added a parent ({kind}) for person {childId}", "person", childId);
        await db.SaveChangesAsync();
        return null;
    }

    public async Task RemoveParentAsync(Guid childId, Guid parentId)
    {
        await db.ParentChildren.Where(x => x.ParentId == parentId && x.ChildId == childId).ExecuteDeleteAsync();
    }

    public async Task<string?> AddPartnerAsync(Guid a, Guid b, PartnershipKind kind, int? startYear, int? endYear)
    {
        if (a == b) return "Choose two different people.";
        var (p1, p2) = a.CompareTo(b) < 0 ? (a, b) : (b, a);
        var existing = await db.Partnerships.SingleOrDefaultAsync(x => x.Person1Id == p1 && x.Person2Id == p2);
        if (existing is null)
        {
            existing = new Partnership { Id = Guid.CreateVersion7(), Person1Id = p1, Person2Id = p2 };
            db.Partnerships.Add(existing);
        }
        (existing.Kind, existing.StartYear, existing.EndYear) = (kind, startYear, endYear);
        audit.Add("person.relate", $"Recorded a partnership ({kind})", "person", a);
        await db.SaveChangesAsync();
        return null;
    }

    public async Task RemovePartnerAsync(Guid a, Guid b)
    {
        await db.Partnerships.Where(x => (x.Person1Id == a && x.Person2Id == b) || (x.Person1Id == b && x.Person2Id == a)).ExecuteDeleteAsync();
    }

    /// <summary>True if <paramref name="candidate"/> is <paramref name="of"/> or one of their descendants.</summary>
    private async Task<bool> IsDescendantAsync(Guid candidate, Guid of)
    {
        var edges = await db.ParentChildren.Select(x => new { x.ParentId, x.ChildId }).ToListAsync();
        var frontier = new Queue<Guid>([of]);
        var seen = new HashSet<Guid>();
        while (frontier.Count > 0)
        {
            var id = frontier.Dequeue();
            if (id == candidate) return true;
            if (!seen.Add(id)) continue;
            foreach (var e in edges.Where(e => e.ParentId == id)) frontier.Enqueue(e.ChildId);
        }
        return false;
    }

    public async Task<Kin?> KinAsync(Guid id)
    {
        var person = await db.People.AsNoTracking().Include(p => p.LinkedUser).SingleOrDefaultAsync(p => p.Id == id);
        if (person is null) return null;
        var parents = await db.ParentChildren.AsNoTracking().Where(x => x.ChildId == id).Select(x => x.Parent!).ToListAsync();
        var children = await db.ParentChildren.AsNoTracking().Where(x => x.ParentId == id).Select(x => x.Child!).ToListAsync();
        var partnerLinks = await db.Partnerships.AsNoTracking().Include(x => x.Person1).Include(x => x.Person2)
            .Where(x => x.Person1Id == id || x.Person2Id == id).ToListAsync();
        var parentIds = parents.Select(p => p.Id).ToList();
        var siblings = await db.ParentChildren.AsNoTracking()
            .Where(x => parentIds.Contains(x.ParentId) && x.ChildId != id).Select(x => x.Child!).Distinct().ToListAsync();
        return new Kin(person, Sort(parents),
            [.. partnerLinks.Select(l => (l.Person1Id == id ? l.Person2! : l.Person1!, l))],
            Sort(children), Sort(siblings));
    }

    private static List<Person> Sort(IEnumerable<Person> people) =>
        [.. people.OrderBy(p => p.BirthYear ?? int.MaxValue).ThenBy(p => p.GivenNames)];

    // ------------------------------------------------------------------ tagging

    /// <summary>Finds a person by the name someone typed ("Rose Smith", "Rose", "Grandma Rose" nickname).</summary>
    public async Task<Person?> FindByNameAsync(string? typed)
    {
        if (string.IsNullOrWhiteSpace(typed)) return null;
        var name = typed.Trim().ToLower();
        var all = await db.People.AsNoTracking().ToListAsync();
        return all.FirstOrDefault(p => p.DisplayName.ToLower() == name)
            ?? all.FirstOrDefault(p => (p.Nickname ?? "").ToLower() == name)
            ?? all.SingleOrDefault(p => p.GivenNames.ToLower() == name);
    }

    public async Task SetMemoryPeopleAsync(Guid memoryId, IEnumerable<Guid> personIds)
    {
        var wanted = personIds.Distinct().ToHashSet();
        var current = await db.MemoryPeople.Where(x => x.MemoryId == memoryId).ToListAsync();
        db.MemoryPeople.RemoveRange(current.Where(x => !wanted.Contains(x.PersonId)));
        foreach (var id in wanted.Where(id => current.All(c => c.PersonId != id)))
            db.MemoryPeople.Add(new MemoryPerson { MemoryId = memoryId, PersonId = id });
        await db.SaveChangesAsync();
    }

    public async Task TagPhotoAsync(Guid attachmentId, Guid personId, bool tagged)
    {
        var existing = await db.AttachmentPeople.SingleOrDefaultAsync(x => x.AttachmentId == attachmentId && x.PersonId == personId);
        if (tagged && existing is null) db.AttachmentPeople.Add(new AttachmentPerson { AttachmentId = attachmentId, PersonId = personId });
        if (!tagged && existing is not null) db.AttachmentPeople.Remove(existing);
        await db.SaveChangesAsync();
    }

    // ------------------------------------------------------------------ display rules

    /// <summary>Living people's birth dates show only the year to non-admins (§7).</summary>
    public static string BirthText(Person p, bool isAdmin)
    {
        if (p.BirthYear is null) return "";
        var birth = p.IsLiving && !isAdmin ? new FuzzyDate(p.BirthYear, null, null, p.BirthCirca, null, null) : p.Birth;
        return birth.Format();
    }

    public static string LifeSpan(Person p, bool isAdmin)
    {
        var born = BirthText(p, isAdmin);
        if (p.IsLiving) return born.Length == 0 ? "" : $"Born {born}";
        var died = p.Death.Format();
        return (born, died) switch
        {
            ("", "") => "",
            (_, "") => $"{born} –",
            ("", _) => $"– {died}",
            _ => $"{born} – {died}",
        };
    }

    private static string? Clean(string? s, int max) =>
        string.IsNullOrWhiteSpace(s) ? null : s.Trim() is var t && t.Length > max ? t[..max] : s.Trim();
}
