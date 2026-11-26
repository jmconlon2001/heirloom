using FamilyMemories.Auth;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace FamilyMemories.Pages.Account;

/// <summary>
/// GET only shows a "Sign me in" button; the token is used up by the POST. Email security
/// scanners open links automatically, and would otherwise burn single-use links before the
/// person ever taps them.
/// </summary>
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed class MagicLinkModel(MagicLinkService links, DeviceService devices) : PageModel
{
    public string? Token { get; private set; }
    public MagicLinkFailure Failure { get; private set; }

    public async Task<IActionResult> OnGetAsync(string? token, CancellationToken ct)
    {
        Token = token;
        Failure = await links.PeekAsync(token, ct);
        // Already signed in on this device as the same person (e.g. tapping a reminder): skip the extra tap.
        if (Failure == MagicLinkFailure.None && User.Identity?.IsAuthenticated == true
            && await links.PeekUserAsync(token, ct) is { } target && target.UserId == User.UserId())
            return LocalRedirect(Url.IsLocalUrl(target.ReturnPath) ? target.ReturnPath! : "/");
        return Page();
    }

    public async Task<IActionResult> OnPostAsync(string? token, CancellationToken ct)
    {
        var result = await links.RedeemAsync(token, HttpContext.Connection.RemoteIpAddress?.ToString(), ct);
        if (!result.Succeeded)
        {
            Failure = result.Failure;
            return Page();
        }

        await devices.SignInNewDeviceAsync(result.User!, HttpContext);
        return LocalRedirect(Url.IsLocalUrl(result.ReturnPath) ? result.ReturnPath! : "/");
    }

    public string FailureHeading => Failure switch
    {
        MagicLinkFailure.AlreadyUsed => "This link has already been used",
        MagicLinkFailure.Expired => "This link is too old",
        MagicLinkFailure.UserDisabled => "This account is paused",
        _ => "This link doesn't work",
    };

    public string FailureMessage => Failure switch
    {
        MagicLinkFailure.AlreadyUsed => "Each sign-in link works only once, to keep your stories safe. We can send you a new one.",
        MagicLinkFailure.Expired => "Sign-in links last 14 days. We can send you a new one right now.",
        MagicLinkFailure.UserDisabled => "Please call the family admin to turn it back on.",
        _ => "It may have been copied only partly. We can send you a new one.",
    };
}
