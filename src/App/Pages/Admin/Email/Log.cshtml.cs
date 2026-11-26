using FamilyMemories.Data;
using FamilyMemories.Email;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace FamilyMemories.Pages.Admin.Email;

public sealed class LogModel(AppDbContext db, IEmailDispatcher dispatcher) : PageModel
{
    public List<EmailLog> Entries { get; private set; } = [];

    public async Task OnGetAsync() =>
        Entries = await db.EmailLog.AsNoTracking().OrderByDescending(e => e.CreatedAt).Take(200).ToListAsync();

    public async Task<IActionResult> OnPostRetryAsync(Guid id)
    {
        await db.EmailLog.Where(e => e.Id == id && e.Status != EmailStatus.Sent)
            .ExecuteUpdateAsync(s => s.SetProperty(e => e.Status, EmailStatus.Queued).SetProperty(e => e.Attempts, 0));
        await dispatcher.DispatchAsync(id);
        TempData["Flash"] = "Trying again.";
        return LocalRedirect("/admin/email/log");
    }
}
