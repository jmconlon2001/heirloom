using FamilyMemories.Auth;
using FamilyMemories.Data;
using FamilyMemories.Email;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace FamilyMemories.Pages.Account;

public sealed class LoginModel(
    UserManager<AppUser> users,
    MagicLinkService links,
    EmailTemplates templates,
    IEmailSender email,
    ILogger<LoginModel> log) : PageModel
{
    [BindProperty] public string? Email { get; set; }
    [BindProperty(SupportsGet = true)] public string? ReturnUrl { get; set; }
    public string? ErrorMessage { get; private set; }

    public IActionResult OnGet()
    {
        if (User.Identity?.IsAuthenticated == true)
            return LocalRedirect(SafeReturnUrl());
        return Page();
    }

    public async Task<IActionResult> OnPostAsync(CancellationToken ct)
    {
        var address = Email?.Trim();
        if (string.IsNullOrEmpty(address) || !address.Contains('@') || address.Length > 254)
        {
            ErrorMessage = "Please type your whole email address, for example rose@example.com.";
            return Page();
        }

        var user = await users.FindByEmailAsync(address);
        if (user is not null)
        {
            var token = await links.IssueAsync(user, MagicLinkPurpose.Login, SafeReturnUrl(),
                HttpContext.Connection.RemoteIpAddress?.ToString(), ct);
            if (token is not null)
            {
                try
                {
                    await email.SendAsync(templates.LoginLink(user.Email!, user.DisplayName, links.BuildUrl(token)), ct);
                }
                catch (Exception ex)
                {
                    log.LogError(ex, "Could not send login link to user {UserId}", user.Id);
                }
            }
            else
            {
                log.LogWarning("Login link limit reached for user {UserId}", user.Id);
            }
        }
        else
        {
            log.LogInformation("Login link requested for an address that has no account");
        }

        // Same answer either way, so the page can't be used to discover who has an account.
        TempData["LoginEmail"] = address;
        return RedirectToPage("CheckEmail");
    }

    private string SafeReturnUrl() => Url.IsLocalUrl(ReturnUrl) ? ReturnUrl! : "/";
}
