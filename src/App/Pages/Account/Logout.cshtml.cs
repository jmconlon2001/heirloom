using FamilyMemories.Auth;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace FamilyMemories.Pages.Account;

public sealed class LogoutModel(DeviceService devices) : PageModel
{
    public IActionResult OnGet() =>
        User.Identity?.IsAuthenticated == true ? Page() : LocalRedirect("/account/signed-out");

    public async Task<IActionResult> OnPostAsync()
    {
        await devices.SignOutAsync(HttpContext);
        return LocalRedirect("/account/signed-out");
    }
}
