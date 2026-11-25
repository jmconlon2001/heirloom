using FamilyMemories.Admin;
using FamilyMemories.Auth;
using FamilyMemories.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace FamilyMemories.Pages.Admin.Users;

public sealed class NewModel(AppDbContext db, UserAdminService admin) : PageModel
{
    [BindProperty] public UserForm Form { get; set; } = new() { Roles = [Roles.Storyteller, Roles.Family] };
    public string? Error { get; private set; }

    public async Task OnGetAsync()
    {
        Form.Sets = await db.PromptSets.AsNoTracking().OrderBy(s => s.Kind).ThenBy(s => s.Name).ToListAsync();
        Form.PromptSetId = Form.Sets.FirstOrDefault(s => s.Kind == PromptSetKind.Core)?.Id;
    }

    public async Task<IActionResult> OnPostAsync(bool sendEmail)
    {
        Error = Form.Validate();
        if (Error is null)
        {
            var result = await admin.InviteAsync(Form.ToInput(), sendEmail, User.DisplayName());
            if (result.Succeeded)
            {
                TempData["Flash"] = sendEmail ? $"{Form.DisplayName} has been added and an invitation is on its way." : $"{Form.DisplayName} has been added.";
                return LocalRedirect($"/admin/users/{result.UserId}");
            }
            Error = result.Error;
        }
        Form.Sets = await db.PromptSets.AsNoTracking().OrderBy(s => s.Kind).ThenBy(s => s.Name).ToListAsync();
        return Page();
    }
}
