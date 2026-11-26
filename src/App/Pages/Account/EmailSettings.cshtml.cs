using FamilyMemories.Auth;
using FamilyMemories.Email;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace FamilyMemories.Pages.Account;

/// <summary>Signed-in shortcut to your own email settings page.</summary>
[Authorize]
public sealed class EmailSettingsModel(EmailLinks links) : PageModel
{
    public IActionResult OnGet() => LocalRedirect($"/email/preferences/{links.Token(User.UserId())}");
}
