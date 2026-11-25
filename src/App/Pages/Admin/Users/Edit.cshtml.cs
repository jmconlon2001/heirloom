using FamilyMemories.Admin;
using FamilyMemories.Auth;
using FamilyMemories.Data;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace FamilyMemories.Pages.Admin.Users;

public sealed class EditModel(AppDbContext db, UserManager<AppUser> users, UserAdminService admin, TimeProvider clock) : PageModel
{
    public AppUser Person { get; private set; } = null!;
    [BindProperty] public UserForm Form { get; set; } = new();
    public string? Error { get; private set; }
    public bool Paused { get; private set; }
    public List<UserDevice> Devices { get; private set; } = [];
    public List<AppUser> Helpers { get; private set; } = [];
    public List<AppUser> HelperCandidates { get; private set; } = [];
    public List<Prompt> OneOffs { get; private set; } = [];

    private async Task<bool> LoadAsync(Guid id, bool fillForm)
    {
        var person = await db.Users.AsNoTracking().SingleOrDefaultAsync(u => u.Id == id);
        if (person is null) return false;
        Person = person;
        Paused = person.LockoutEnd > clock.GetUtcNow();
        var now = clock.GetUtcNow().UtcDateTime;
        Devices = await db.UserDevices.AsNoTracking()
            .Where(d => d.UserId == id && d.RevokedAt == null && d.ExpiresAt > now)
            .OrderByDescending(d => d.LastSeenAt).ToListAsync();
        Helpers = await db.StorytellerHelpers.Where(h => h.StorytellerId == id).Select(h => h.Helper!).AsNoTracking().ToListAsync();
        var helperIds = Helpers.Select(h => h.Id).ToHashSet();
        HelperCandidates = await db.Users.AsNoTracking().Where(u => u.Id != id).OrderBy(u => u.DisplayName).ToListAsync();
        HelperCandidates.RemoveAll(u => helperIds.Contains(u.Id));
        OneOffs = await db.Prompts.AsNoTracking().Include(p => p.AskedByUser)
            .Where(p => p.ForUserId == id).OrderBy(p => p.CreatedAt).ToListAsync();

        Form.Sets = await db.PromptSets.AsNoTracking().OrderBy(s => s.Kind).ThenBy(s => s.Name).ToListAsync();
        if (fillForm)
        {
            Form.DisplayName = person.DisplayName;
            Form.Email = person.Email ?? "";
            Form.Roles = [.. await users.GetRolesAsync(person)];
            Form.PromptSetId = person.PromptSetId;
            Form.Pacing = person.Pacing;
        }
        return true;
    }

    public async Task<IActionResult> OnGetAsync(Guid id) => await LoadAsync(id, true) ? Page() : NotFound();

    public async Task<IActionResult> OnPostSaveAsync(Guid id)
    {
        Error = Form.Validate();
        if (Error is null)
        {
            var result = await admin.UpdateAsync(id, Form.ToInput(), User.UserId());
            if (result.Succeeded) return Done(id, "Changes saved.");
            Error = result.Error;
        }
        return await LoadAsync(id, false) ? Page() : NotFound();
    }

    public async Task<IActionResult> OnPostInviteAsync(Guid id)
    {
        var person = await users.FindByIdAsync(id.ToString());
        if (person is null) return NotFound();
        await admin.SendInvitationAsync(person, User.DisplayName());
        return Done(id, $"A new invitation is on its way to {person.Email}.");
    }

    public async Task<IActionResult> OnPostAddQuestionAsync(Guid id, string? newQuestion)
    {
        if (string.IsNullOrWhiteSpace(newQuestion)) return Done(id, "Please type a question first.");
        await admin.AddOneOffPromptAsync(id, newQuestion, null, User.UserId());
        return Done(id, "Question added. It's now first on their list.");
    }

    public async Task<IActionResult> OnPostAddHelperAsync(Guid id, Guid helperId)
    {
        var result = await admin.AddHelperAsync(id, helperId);
        return Done(id, result.Succeeded ? "Helper added." : result.Error!);
    }

    public async Task<IActionResult> OnPostRemoveHelperAsync(Guid id, Guid helperId)
    {
        await admin.RemoveHelperAsync(id, helperId);
        return Done(id, "Helper removed.");
    }

    public async Task<IActionResult> OnPostRevokeDeviceAsync(Guid id, Guid deviceId)
    {
        await admin.RevokeDeviceAsync(id, deviceId);
        return Done(id, "That device has been signed out.");
    }

    public async Task<IActionResult> OnPostPauseAsync(Guid id, bool paused)
    {
        var result = await admin.SetPausedAsync(id, paused, User.UserId());
        return Done(id, result.Succeeded ? (paused ? "Account paused." : "Account unpaused.") : result.Error!);
    }

    private IActionResult Done(Guid id, string message)
    {
        TempData["Flash"] = message;
        return LocalRedirect($"/admin/users/{id}");
    }
}
