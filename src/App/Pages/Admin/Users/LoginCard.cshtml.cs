using FamilyMemories.Admin;
using FamilyMemories.Auth;
using FamilyMemories.Configuration;
using FamilyMemories.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using QRCoder;

namespace FamilyMemories.Pages.Admin.Users;

/// <summary>
/// POST-only so that reloading, link previews or prefetching never mint extra sign-in links.
/// Each card holds a single-use device-enrollment link.
/// </summary>
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed class LoginCardModel(AppDbContext db, UserAdminService admin, MagicLinkService links, AppOptions options, TimeProvider clock) : PageModel
{
    public Guid Id { get; private set; }
    public string Name { get; private set; } = "";
    public string QrDataUri { get; private set; } = "";
    public string ExpiresOn { get; private set; } = "";

    public IActionResult OnGet(Guid id) => LocalRedirect($"/admin/users/{id}");

    public async Task<IActionResult> OnPostAsync(Guid id)
    {
        var user = await db.Users.SingleOrDefaultAsync(u => u.Id == id);
        if (user is null) return NotFound();
        var token = await admin.CreateLoginCardAsync(user);
        if (token is null) return NotFound();

        Id = id;
        Name = user.DisplayName;
        ExpiresOn = clock.GetUtcNow().Add(options.LoginCardLifetime).ToLocalTime().ToString("MMMM d, yyyy");
        QrDataUri = QrPng(links.BuildUrl(token).ToString());
        return Page();
    }

    internal static string QrPng(string text)
    {
        using var generator = new QRCodeGenerator();
        using var data = generator.CreateQrCode(text, QRCodeGenerator.ECCLevel.M);
        var png = new PngByteQRCode(data).GetGraphic(12);
        return "data:image/png;base64," + Convert.ToBase64String(png);
    }
}
