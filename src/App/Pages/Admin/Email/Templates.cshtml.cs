using FamilyMemories.Admin;
using FamilyMemories.Auth;
using FamilyMemories.Data;
using FamilyMemories.Email;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace FamilyMemories.Pages.Admin.Email;

public sealed class TemplatesModel(AppDbContext db, EmailTemplates templates, EmailService email, AuditLog audit, TimeProvider clock) : PageModel
{
    public EmailKind? Kind { get; private set; }
    public HashSet<EmailKind> Customised { get; private set; } = [];
    public IEnumerable<string> Fields { get; private set; } = [];
    public string? Error { get; private set; }
    [BindProperty] public string? Subject { get; set; }
    [BindProperty] public string? TextBody { get; set; }
    [BindProperty] public string? HtmlBody { get; set; }

    private async Task LoadAsync(EmailKind? kind, bool fill)
    {
        Kind = kind;
        Customised = [.. await db.EmailTemplates.Select(t => t.Kind).ToListAsync()];
        if (kind is not { } k) return;
        Fields = templates.SampleModel(k).Keys;
        if (fill)
        {
            var t = await templates.GetAsync(k);
            (Subject, TextBody, HtmlBody) = (t.Subject, t.TextBody, t.HtmlBody);
        }
    }

    public async Task<IActionResult> OnGetAsync(EmailKind? kind)
    {
        if (kind is { } k && !EmailTemplates.Defaults.ContainsKey(k)) return NotFound();
        await LoadAsync(kind, true);
        return Page();
    }

    private string? Validate()
    {
        if (string.IsNullOrWhiteSpace(Subject)) return "The subject can't be empty.";
        var error = EmailTemplates.Validate(Subject) ?? EmailTemplates.Validate(TextBody ?? "") ?? EmailTemplates.Validate(HtmlBody ?? "");
        return error is null ? null : $"There's a mistake in the template: {error}";
    }

    public async Task<IActionResult> OnPostSaveAsync(EmailKind kind)
    {
        await LoadAsync(kind, false);
        if (Validate() is { } error) { Error = error; return Page(); }
        var row = await db.EmailTemplates.SingleOrDefaultAsync(t => t.Kind == kind);
        if (row is null) db.EmailTemplates.Add(row = new EmailTemplateOverride { Kind = kind });
        (row.Subject, row.TextBody, row.HtmlBody, row.UpdatedAt) = (Subject!, TextBody ?? "", HtmlBody ?? "", clock.GetUtcNow().UtcDateTime);
        audit.Add("email.template", $"Changed the wording of \"{EmailTemplates.Descriptions[kind]}\"");
        await db.SaveChangesAsync();
        TempData["Flash"] = "Saved.";
        return LocalRedirect($"/admin/email/templates/{kind}");
    }

    public async Task<IActionResult> OnPostResetAsync(EmailKind kind)
    {
        await db.EmailTemplates.Where(t => t.Kind == kind).ExecuteDeleteAsync();
        await audit.WriteAsync("email.template-reset", $"Put back the original wording of \"{EmailTemplates.Descriptions[kind]}\"");
        TempData["Flash"] = "The original wording is back.";
        return LocalRedirect($"/admin/email/templates/{kind}");
    }

    public async Task<IActionResult> OnPostTestAsync(EmailKind kind)
    {
        await LoadAsync(kind, false);
        if (Validate() is { } error) { Error = error; return Page(); }
        var me = await db.Users.SingleAsync(u => u.Id == User.UserId());
        var log = await email.SendAsync(kind, new Recipient(me.Email!, me.DisplayName, me.Id), templates.SampleModel(kind),
            new EmailTemplate(Subject!, TextBody ?? "", HtmlBody ?? ""));
        var status = await db.EmailLog.Where(e => e.Id == log.Id).Select(e => new { e.Status, e.Error }).SingleAsync();
        TempData["Flash"] = status.Status == EmailStatus.Failed ? $"The test couldn't be sent: {status.Error}" : $"A test (with example details) is on its way to {me.Email}.";
        return LocalRedirect($"/admin/email/templates/{kind}");
    }
}
