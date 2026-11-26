using FamilyMemories.Auth;
using FamilyMemories.Data;
using FamilyMemories.Family;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace FamilyMemories.Pages.People;

/// <summary>Add or edit a person. Any family member can; removing someone is admin-only. All changes are audit-logged.</summary>
public sealed class EditModel(AppDbContext db, PeopleService people, TimeProvider clock) : PageModel
{
    public bool IsNew { get; private set; }
    public Person? Person { get; private set; }
    public Kin? Kin { get; private set; }
    public List<AppUser> Users { get; private set; } = [];
    public List<Person> Others { get; private set; } = [];
    public List<Attachment> PhotoChoices { get; private set; } = [];
    public List<PersonLink> Links { get; private set; } = [];
    public string? Error { get; private set; }

    [BindProperty] public string? GivenNames { get; set; }
    [BindProperty] public string? Surname { get; set; }
    [BindProperty] public string? BirthSurname { get; set; }
    [BindProperty] public string? Nickname { get; set; }
    [BindProperty] public Sex Sex { get; set; }
    [BindProperty] public int? BirthYear { get; set; }
    [BindProperty] public int? BirthMonth { get; set; }
    [BindProperty] public int? BirthDay { get; set; }
    [BindProperty] public bool BirthCirca { get; set; }
    [BindProperty] public string? BirthPlace { get; set; }
    [BindProperty] public bool IsLiving { get; set; } = true;
    [BindProperty] public int? DeathYear { get; set; }
    [BindProperty] public string? DeathPlace { get; set; }
    [BindProperty] public string? Bio { get; set; }
    [BindProperty] public Guid? LinkedUserId { get; set; }
    [BindProperty] public Guid? PhotoAttachmentId { get; set; }

    private async Task<bool> LoadAsync(string id, bool fill)
    {
        IsNew = id == "new";
        if (User.IsAdmin()) Users = await db.Users.AsNoTracking().OrderBy(u => u.DisplayName).ToListAsync();
        if (IsNew) return true;
        if (!Guid.TryParse(id, out var pid)) return false;
        Kin = await people.KinAsync(pid);
        if (Kin is null) return false;
        Person = Kin.Person;
        Links = await db.PersonLinks.AsNoTracking().Where(l => l.PersonId == pid).OrderBy(l => l.SortOrder).ToListAsync();
        Others = await db.People.AsNoTracking().Where(p => p.Id != pid).OrderBy(p => p.GivenNames).ThenBy(p => p.Surname).ToListAsync();
        PhotoChoices = await db.Attachments.AsNoTracking()
            .Where(a => a.Kind == AttachmentKind.Photo && a.State == ProcessingState.Ready && a.RemovedAt == null
                        && (db.AttachmentPeople.Any(x => x.AttachmentId == a.Id && x.PersonId == pid) || a.Id == Person.PhotoAttachmentId))
            .OrderByDescending(a => a.CreatedAt).Take(40).ToListAsync();
        if (fill)
        {
            var p = Person;
            (GivenNames, Surname, BirthSurname, Nickname, Sex) = (p.GivenNames, p.Surname, p.BirthSurname, p.Nickname, p.Sex);
            (BirthYear, BirthMonth, BirthDay, BirthCirca, BirthPlace) = (p.BirthYear, p.BirthMonth, p.BirthDay, p.BirthCirca, p.BirthPlace);
            (IsLiving, DeathYear, DeathPlace, Bio, LinkedUserId, PhotoAttachmentId) = (p.IsLiving, p.DeathYear, p.DeathPlace, p.Bio, p.LinkedUserId, p.PhotoAttachmentId);
        }
        return true;
    }

    public async Task<IActionResult> OnGetAsync(string id) => await LoadAsync(id, true) ? Page() : NotFound();

    public async Task<IActionResult> OnPostSaveAsync(string id)
    {
        if (!await LoadAsync(id, false)) return NotFound();
        if (string.IsNullOrWhiteSpace(GivenNames) && string.IsNullOrWhiteSpace(Surname))
        {
            Error = "Please type at least a first or last name.";
            return Page();
        }
        var year = clock.GetUtcNow().Year;
        if (!User.IsAdmin()) LinkedUserId = Person?.LinkedUserId; // only admins link accounts
        if (LinkedUserId is { } uid && await db.People.AnyAsync(p => p.LinkedUserId == uid && p.Id != (Person == null ? Guid.Empty : Person.Id)))
        {
            Error = "That account is already linked to someone else in the tree.";
            return Page();
        }
        if (PhotoAttachmentId is { } photoId && PhotoChoices.All(a => a.Id != photoId)) PhotoAttachmentId = Person?.PhotoAttachmentId;

        var input = new PersonInput(GivenNames ?? "", Surname ?? "", BirthSurname, Nickname, Sex,
            FuzzyDate.Normalize(BirthYear, BirthMonth, BirthDay, BirthCirca, null, null, year), BirthPlace,
            IsLiving, FuzzyDate.Normalize(DeathYear, null, null, false, null, null, year), DeathPlace, Bio, LinkedUserId, PhotoAttachmentId);
        Guid targetId;
        if (IsNew) targetId = (await people.CreateAsync(input)).Id;
        else { await people.UpdateAsync(Person!.Id, input); targetId = Person.Id; }
        TempData["Flash"] = IsNew ? "Added. You can now connect them to their family." : "Saved.";
        return LocalRedirect(IsNew ? $"/people/{targetId}/edit#rel-heading" : $"/people/{targetId}");
    }

    public async Task<IActionResult> OnPostRelateAsync(string id, string relation, Guid otherId)
    {
        if (!await LoadAsync(id, false) || Person is null) return NotFound();
        var me = Person.Id;
        var error = relation switch
        {
            "parent" => await people.AddParentAsync(me, otherId, ParentKind.Biological),
            "adoptive-parent" => await people.AddParentAsync(me, otherId, ParentKind.Adoptive),
            "step-parent" => await people.AddParentAsync(me, otherId, ParentKind.Step),
            "child" => await people.AddParentAsync(otherId, me, ParentKind.Biological),
            "spouse" => await people.AddPartnerAsync(me, otherId, PartnershipKind.Married, null, null),
            "partner" => await people.AddPartnerAsync(me, otherId, PartnershipKind.Partnered, null, null),
            "ex" => await people.AddPartnerAsync(me, otherId, PartnershipKind.Divorced, null, null),
            _ => "Please choose a kind of connection.",
        };
        TempData["Flash"] = error ?? "Connection added.";
        return LocalRedirect($"/people/{me}/edit#rel-heading");
    }

    public async Task<IActionResult> OnPostUnrelateAsync(string id, string removeId)
    {
        if (!await LoadAsync(id, false) || Person is null) return NotFound();
        var parts = removeId.Split(':');
        if (parts.Length == 2 && Guid.TryParse(parts[1], out var other))
        {
            if (parts[0] == "parent") await people.RemoveParentAsync(Person.Id, other);
            if (parts[0] == "child") await people.RemoveParentAsync(other, Person.Id);
            if (parts[0] == "partner") await people.RemovePartnerAsync(Person.Id, other);
            TempData["Flash"] = "Connection removed.";
        }
        return LocalRedirect($"/people/{Person.Id}/edit#rel-heading");
    }

    public async Task<IActionResult> OnPostAddLinkAsync(string id, string? linkLabel, string? linkUrl)
    {
        if (!await LoadAsync(id, false) || Person is null) return NotFound();
        var error = await people.AddLinkAsync(Person.Id, linkLabel, linkUrl);
        TempData["Flash"] = error ?? "Link added.";
        return LocalRedirect($"/people/{Person.Id}/edit#links-heading");
    }

    public async Task<IActionResult> OnPostRemoveLinkAsync(string id, Guid linkId)
    {
        if (!await LoadAsync(id, false) || Person is null) return NotFound();
        await people.RemoveLinkAsync(Person.Id, linkId);
        TempData["Flash"] = "Link removed.";
        return LocalRedirect($"/people/{Person.Id}/edit#links-heading");
    }

    public async Task<IActionResult> OnPostDeleteAsync(string id)
    {
        if (!User.IsAdmin()) return Forbid();
        if (!await LoadAsync(id, false) || Person is null) return NotFound();
        await people.DeleteAsync(Person.Id);
        TempData["Flash"] = $"{Person.DisplayName} was removed from the family tree.";
        return LocalRedirect("/people");
    }
}
