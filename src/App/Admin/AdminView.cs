using FamilyMemories.Auth;
using FamilyMemories.Data;
using Microsoft.EntityFrameworkCore;

namespace FamilyMemories.Admin;

/// <summary>
/// How the admin pages look for one admin: Basic (the same calm, large look as the rest of the site) or
/// Complex (a denser, sidebar-and-tables look like a typical admin console). Saved per admin in the
/// settings table, so it follows them to every device.
/// </summary>
public enum AdminViewMode { Basic, Complex }

public sealed class AdminViewService(AppDbContext db, TimeProvider clock)
{
    public static string Key(Guid userId) => $"admin-view:{userId:N}";
    private const string CacheKey = "admin-view-mode";

    public async Task<AdminViewMode> GetAsync(HttpContext http)
    {
        if (http.Items[CacheKey] is AdminViewMode cached) return cached;
        var mode = AdminViewMode.Basic;
        if (http.User.IsInRole(Roles.Admin))
        {
            var id = http.User.UserId();
            var value = await db.Settings.AsNoTracking().Where(s => s.Key == Key(id)).Select(s => s.Value).SingleOrDefaultAsync();
            if (Enum.TryParse<AdminViewMode>(value, out var saved)) mode = saved;
        }
        http.Items[CacheKey] = mode;
        return mode;
    }

    public async Task SetAsync(Guid userId, AdminViewMode mode)
    {
        var key = Key(userId);
        var row = await db.Settings.SingleOrDefaultAsync(s => s.Key == key);
        if (row is null) db.Settings.Add(row = new AppSetting { Key = key });
        row.Value = mode.ToString();
        row.UpdatedAt = clock.GetUtcNow().UtcDateTime;
        await db.SaveChangesAsync();
    }

    /// <summary>Pages that get the admin look: everything under /admin, plus /status for admins.</summary>
    public static bool IsAdminArea(HttpContext http) =>
        http.User.IsInRole(Roles.Admin) && !ViewAs.IsActive(http)
        && (http.Request.Path.StartsWithSegments("/admin") || http.Request.Path.StartsWithSegments("/status"));
}
