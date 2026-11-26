using FamilyMemories.Auth;
using FamilyMemories.Data;
using Microsoft.EntityFrameworkCore;

namespace FamilyMemories.Web;

/// <summary>
/// How the whole site looks for one signed-in person.
/// Simple (the default): large text, one thing at a time, the top menu.
/// Detailed: a sidebar with every section (and the admin sections for admins), more on screen, compact tables.
/// Saved per person in the settings table, so it follows them to every device.
/// </summary>
public enum ViewMode { Simple, Detailed }

public sealed class ViewModeService(AppDbContext db, TimeProvider clock)
{
    public static readonly string[] Labels = ["Simple", "Detailed"];
    public static string Key(Guid userId) => $"view-mode:{userId:N}";
    private const string CacheKey = "view-mode";

    public async Task<ViewMode> GetAsync(HttpContext http)
    {
        if (http.Items[CacheKey] is ViewMode cached) return cached;
        var mode = ViewMode.Simple;
        // While an admin is "viewing as" someone, show the plain family look.
        if (http.User.Identity?.IsAuthenticated == true && !FamilyMemories.Admin.ViewAs.IsActive(http))
        {
            var key = Key(http.User.UserId());
            var value = await db.Settings.AsNoTracking().Where(s => s.Key == key).Select(s => s.Value).SingleOrDefaultAsync();
            if (Enum.TryParse<ViewMode>(value, out var saved)) mode = saved;
        }
        http.Items[CacheKey] = mode;
        return mode;
    }

    public async Task SetAsync(Guid userId, ViewMode mode)
    {
        var key = Key(userId);
        var row = await db.Settings.SingleOrDefaultAsync(s => s.Key == key);
        if (row is null) db.Settings.Add(row = new AppSetting { Key = key });
        row.Value = mode.ToString();
        row.UpdatedAt = clock.GetUtcNow().UtcDateTime;
        await db.SaveChangesAsync();
    }

    /// <summary>Wide admin pages: everything under /admin, plus /status for admins.</summary>
    public static bool IsAdminArea(HttpContext http) =>
        http.User.IsInRole(Roles.Admin) && !FamilyMemories.Admin.ViewAs.IsActive(http)
        && (http.Request.Path.StartsWithSegments("/admin") || http.Request.Path.StartsWithSegments("/status"));
}
