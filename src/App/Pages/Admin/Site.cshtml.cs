using FamilyMemories.Admin;
using FamilyMemories.Configuration;
using FamilyMemories.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace FamilyMemories.Pages.Admin;

public sealed class SiteModel(AppDbContext db, AppOptions options, AuditLog audit, TimeProvider clock) : PageModel
{
    [BindProperty] public string? SiteName { get; set; }
    [BindProperty] public string? HelpName { get; set; }
    [BindProperty] public string? HelpPhone { get; set; }
    [BindProperty] public int TextSize { get; set; }
    [BindProperty] public bool HighContrast { get; set; }
    [BindProperty] public string? MainTreeUrl { get; set; }

    public Dictionary<string, string> Errors { get; } = [];
    public AppOptions Options => options;

    public async Task OnGetAsync()
    {
        var s = await SiteSettings.LoadAsync(db);
        (SiteName, HelpName, HelpPhone, TextSize, HighContrast, MainTreeUrl) = (s.AppName, s.HelpName, s.HelpPhone, s.TextSize, s.HighContrast, s.MainTreeUrl);
    }

    public async Task<IActionResult> OnPostAsync()
    {
        static string? Clean(string? v, int max) => string.IsNullOrWhiteSpace(v) ? null : v.Trim() is var t && t.Length > max ? t[..max] : v.Trim();
        SiteName = Clean(SiteName, 80);
        HelpName = Clean(HelpName, 80);
        HelpPhone = Clean(HelpPhone, 40);
        if (HelpPhone is not null && !SiteSettings.IsPhoneNumber(HelpPhone))
            Errors[nameof(HelpPhone)] = "Please type a phone number using digits, like (555) 010-0199.";
        if (TextSize is < 0 or > 2) TextSize = 0;
        MainTreeUrl = Clean(MainTreeUrl, 500);
        if (MainTreeUrl is not null && !FamilyMemories.Family.Gedcom.IsWebLink(MainTreeUrl))
            Errors[nameof(MainTreeUrl)] = "Please paste a whole web address, starting with https://";
        if (Errors.Count > 0) return Page();

        var settings = new SiteSettings(SiteName, HelpName, HelpPhone, TextSize, HighContrast, MainTreeUrl);
        await settings.SaveAsync(db, audit, clock.GetUtcNow().UtcDateTime);
        settings.ApplyTo(options);
        TempData["Flash"] = "Saved. Every page, email and login card now uses these.";
        return LocalRedirect("/admin/site");
    }
}
