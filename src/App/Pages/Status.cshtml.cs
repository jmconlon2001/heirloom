using FamilyMemories.Web;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace FamilyMemories.Pages;

/// <summary>"Is everything working?" Anyone sees each part's state; admins also see the details.</summary>
public sealed class StatusModel(SystemStatusService status) : PageModel
{
    public SystemStatusReport Report { get; private set; } = null!;
    public bool Details { get; private set; }

    public async Task OnGetAsync(CancellationToken ct)
    {
        if (User.IsInRole(Data.Roles.Admin) && Request.Query.ContainsKey("refresh")) status.Invalidate();
        Report = await status.GetAsync(ct);
        Details = status.MaySeeDetails(HttpContext);
        Response.Headers.CacheControl = "no-store";
        if (Report.Level == StatusLevel.Problem) Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
    }
}
