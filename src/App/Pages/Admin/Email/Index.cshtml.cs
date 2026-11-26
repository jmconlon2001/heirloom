using FamilyMemories.Admin;
using FamilyMemories.Auth;
using FamilyMemories.Configuration;
using FamilyMemories.Data;
using FamilyMemories.Email;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace FamilyMemories.Pages.Admin.Email;

public sealed class IndexModel(SmtpSettingsStore store, EmailService email, AppDbContext db, AuditLog audit, TimeProvider clock) : PageModel
{
    [BindProperty] public string? Host { get; set; }
    [BindProperty] public int Port { get; set; } = 587;
    [BindProperty] public SmtpSecurity Security { get; set; } = SmtpSecurity.StartTls;
    [BindProperty] public string? Username { get; set; }
    [BindProperty] public string? Password { get; set; }
    [BindProperty] public string? FromAddress { get; set; }
    [BindProperty] public string? FromName { get; set; }
    public bool HasPassword { get; private set; }
    public bool FromAdminUi { get; private set; }
    public string MyEmail { get; private set; } = "";
    public string? Error { get; private set; }

    private async Task LoadAsync(bool fill)
    {
        var s = await store.GetAsync();
        HasPassword = s.Password is not null;
        FromAdminUi = await store.HasSavedSettingsAsync();
        MyEmail = await db.Users.Where(u => u.Id == User.UserId()).Select(u => u.Email!).SingleAsync();
        if (fill) (Host, Port, Security, Username, FromAddress, FromName) = (s.Host, s.Port, s.Security, s.Username, s.FromAddress, s.FromName);
    }

    public async Task OnGetAsync() => await LoadAsync(true);

    public async Task<IActionResult> OnPostSaveAsync()
    {
        await LoadAsync(false);
        if (string.IsNullOrWhiteSpace(Host) || string.IsNullOrWhiteSpace(FromAddress) || Port is < 1 or > 65535)
        {
            Error = "Please fill in the server, a port between 1 and 65535, and the send-from address.";
            return Page();
        }
        await store.SaveAsync(new SmtpOptions
        {
            Host = Host, Port = Port, Security = Security, Username = Username, FromAddress = FromAddress, FromName = FromName ?? "",
        }, string.IsNullOrEmpty(Password) ? null : Password, clock.GetUtcNow().UtcDateTime);
        await audit.WriteAsync("email.smtp", $"Changed the mail server to {Host}:{Port}");
        TempData["Flash"] = "Mail settings saved. Send a test email to check them.";
        return LocalRedirect("/admin/email");
    }

    public async Task<IActionResult> OnPostTestAsync(string? testTo)
    {
        if (string.IsNullOrWhiteSpace(testTo) || !testTo.Contains('@'))
        {
            TempData["Flash"] = "Please type an email address to send the test to.";
            return LocalRedirect("/admin/email");
        }
        var log = await email.SendAsync(EmailKind.Test, new Recipient(testTo.Trim(), User.DisplayName(), null), new Dictionary<string, object?>());
        var result = await db.EmailLog.AsNoTracking().SingleAsync(e => e.Id == log.Id);
        TempData["Flash"] = result.Status switch
        {
            EmailStatus.Sent => $"Test email sent to {testTo}. Check that inbox (and the spam folder).",
            EmailStatus.Failed => $"The test email could not be sent: {result.Error}",
            _ => $"The test email to {testTo} is on its way; see Sent emails if it doesn't arrive.",
        };
        return LocalRedirect("/admin/email");
    }
}
