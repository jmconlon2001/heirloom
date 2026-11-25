using FamilyMemories.Web;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace FamilyMemories.Pages;

/// <summary>No-JavaScript fallback for the text size and contrast buttons.</summary>
public sealed class DisplayModel : PageModel
{
    public IActionResult OnGet() => LocalRedirect("/");

    public IActionResult OnPost(int? size, string? contrast, string? returnUrl)
    {
        var current = DisplayPreferences.From(HttpContext);
        var next = current with
        {
            TextSize = size is >= 0 and <= 2 ? size.Value : current.TextSize,
            HighContrast = contrast switch { "on" => true, "off" => false, _ => current.HighContrast },
        };
        next.Save(HttpContext);
        return LocalRedirect(Url.IsLocalUrl(returnUrl) ? returnUrl : "/");
    }
}
